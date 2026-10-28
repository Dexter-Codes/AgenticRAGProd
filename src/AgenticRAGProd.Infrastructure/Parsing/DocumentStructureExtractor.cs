using System.Text.RegularExpressions;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Infrastructure.Parsing;

internal sealed partial class DocumentStructureExtractor(IPageLayoutAnalyzer layoutAnalyzer)
{
    public (IReadOnlyList<DocumentBlock> Blocks, IReadOnlyList<string> Warnings) Extract(
        string text, IReadOnlyList<ExtractedWord>? words = null, IReadOnlyList<double>? documentFontSizes = null)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var warnings = new List<string>();
        var tables = FindTables(text, words ?? [], warnings);
        var fontHeadings = FindFontHeadings(words ?? [], documentFontSizes);
        var blocks = new List<DocumentBlock>();
        var offset = 0;
        foreach (var table in tables.OrderBy(table => table.Start))
        {
            AddText(text[offset..table.Start], fontHeadings, blocks, warnings);
            blocks.Add(table.Block);
            offset = table.End;
        }
        AddText(text[offset..], fontHeadings, blocks, warnings);
        return (blocks, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private List<TableSpan> FindTables(
        string text, IReadOnlyList<ExtractedWord> words, List<string> warnings)
    {
        var rows = layoutAnalyzer.Analyze(words);
        var spans = new List<TableSpan>();
        var heights = words.Where(word => word.Top > word.Bottom)
            // Glyph boxes can be much shorter than the line's nominal font.
            // Include font size so ordinary line leading is not mistaken for
            // an interruption in an otherwise aligned table.
            .Select(word => Math.Max(word.FontSize, word.Top - word.Bottom)).Order().ToArray();
        var height = heights.Length == 0 ? 10 : heights[heights.Length / 2];
        for (var start = 0; start < rows.Count; start++)
        {
            if (rows[start].Cells.Count < 2) continue;
            var end = start + 1;
            while (end < rows.Count && rows[end].Cells.Count >= 2
                && rows[end - 1].Bottom - rows[end].Bottom <= height * 2.5)
                end++;
            var group = rows.Skip(start).Take(end - start).ToArray();
            start = end - 1;
            var header = group[0];
            var headerWords = words.Where(word => Math.Abs(word.Bottom - header.Bottom) <= height * .35).ToArray();
            var reliable = group.Length >= 3 && headerWords.Length > 0
                && headerWords.All(word => word.IsBold)
                && group.Skip(1).All(row => row.Cells.Count == header.Cells.Count
                    && row.Cells.Select((cell, column) =>
                        Math.Abs(cell.Left - header.Cells[column].Left) <= height * .5
                        && (column == row.Cells.Count - 1 || cell.Right < header.Cells[column + 1].Left))
                        .All(aligned => aligned));
            // Mapping is accepted only if all source tokens occupy one unique,
            // contiguous span in the text extractor's reading order as well.
            var tokens = group.SelectMany(row => row.Cells)
                .SelectMany(cell => Whitespace().Split(cell.Text).Where(token => token.Length > 0));
            var pattern = string.Join(@"\s+", tokens.Select(Regex.Escape));
            var matches = Regex.Matches(text, @"(?<!\S)" + pattern + @"(?!\S)");
            if (!reliable || matches.Count != 1)
            {
                warnings.Add("Uncertain table or multi-column layout: original reading-order text retained; no cell mapping inferred.");
                continue;
            }
            var match = matches[0];
            if (spans.Any(span => span.Start < match.Index + match.Length && match.Index < span.End))
            {
                warnings.Add("Overlapping layout evidence: original text retained for the ambiguous region.");
                continue;
            }
            string RowText(LayoutRow row) => string.Join(" | ", row.Cells.Select(cell => cell.Text));
            spans.Add(new TableSpan(match.Index, match.Index + match.Length,
                new DocumentBlock(DocumentBlockKind.Table, match.Value,
                    TableHeader: RowText(header), TableRows: group.Skip(1).Select(RowText).ToArray())));
        }
        return spans;
    }

    private static void AddText(string text, IReadOnlyDictionary<string, int> fontHeadings,
        List<DocumentBlock> blocks, List<string> warnings)
    {
        var lines = text.Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0)
                blocks.Add(new DocumentBlock(DocumentBlockKind.Prose, string.Join("\n", paragraph)));
            paragraph.Clear();
        }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) { Flush(); continue; }
            // Explicit pipe tables are supported only with a separator and a
            // consistent cell count. Malformed rows stay untouched as prose.
            if (line.Contains('|') && index + 1 < lines.Length && TableSeparator().IsMatch(lines[index + 1].Trim()))
            {
                Flush();
                var end = index + 2;
                while (end < lines.Length && lines[end].Contains('|') && !string.IsNullOrWhiteSpace(lines[end])) end++;
                var count = Cells(line).Length;
                var rows = lines[(index + 2)..end];
                if (count >= 2 && Cells(lines[index + 1]).Length == count
                    && rows.Length > 0 && rows.All(row => Cells(row).Length == count))
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Table,
                        string.Join("\n", lines[index..end]), TableHeader: line + "\n" + lines[index + 1].Trim(),
                        TableRows: rows.Select(row => row.Trim()).ToArray()));
                else
                {
                    blocks.Add(new DocumentBlock(DocumentBlockKind.Prose, string.Join("\n", lines[index..end])));
                    warnings.Add("Inconsistent pipe table: original text retained; no cell mapping inferred.");
                }
                index = end - 1;
                continue;
            }
            var level = HeadingLevel(line, index == 0 || string.IsNullOrWhiteSpace(lines[index - 1]),
                index + 1 == lines.Length || string.IsNullOrWhiteSpace(lines[index + 1]), fontHeadings);
            var setext = index + 1 < lines.Length && Setext().IsMatch(lines[index + 1].Trim());
            if (setext) level = lines[index + 1].Trim()[0] == '=' ? 1 : 2;
            if (level > 0)
            {
                Flush();
                blocks.Add(new DocumentBlock(DocumentBlockKind.Heading,
                    setext ? line + "\n" + lines[++index].Trim() : line, level));
            }
            else
            {
                if (ListItem().IsMatch(line)) Flush();
                paragraph.Add(line);
            }
        }
        Flush();
    }

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|');

    private IReadOnlyDictionary<string, int> FindFontHeadings(IReadOnlyList<ExtractedWord> words, IReadOnlyList<double>? documentFontSizes)
    {
        // Analyze once per page, rather than re-sorting every word for each line.
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var sizes = (documentFontSizes ?? words.Select(word => word.FontSize).ToArray())
            .Where(size => size > 0).Order().ToArray();
        if (sizes.Length == 0) return result;
        var bodySize = sizes[sizes.Length / 2];
        var headingSizes = sizes.Where(size => size > bodySize * 1.2).Distinct().OrderDescending().ToArray();
        foreach (var row in layoutAnalyzer.Analyze(words).Where(row => row.Cells.Count == 1))
        {
            var rowWords = words.Where(word => Math.Abs(word.Bottom - row.Bottom) <= Math.Max(1.5, (word.Top - word.Bottom) * .35)).ToArray();
            if (rowWords.Length == 0 || !rowWords.All(word => word.IsBold || word.FontSize > bodySize * 1.2)) continue;
            var rank = Array.IndexOf(headingSizes, rowWords.Min(word => word.FontSize));
            result[row.Cells[0].Text] = rank >= 0 ? Math.Min(6, rank + 1) : Math.Min(6, headingSizes.Length + 1);
        }
        return result;
    }

    private static int HeadingLevel(string line, bool blankBefore, bool blankAfter, IReadOnlyDictionary<string, int> fontHeadings)
    {
        var markdown = MarkdownHeading().Match(line);
        if (markdown.Success) return markdown.Groups[1].Length;
        if (line.Length > 160 || line.EndsWith('.') || line.EndsWith(';') || line.EndsWith('?')) return 0;
        if (AppendixHeading().IsMatch(line)) return 1;
        var numbered = NumberedHeading().Match(line);
        if (numbered.Success)
            return numbered.Groups[1].Value.Count(character => character == '.') + 1;
        var lineWords = Whitespace().Split(line);
        // PDF font evidence does not depend on blank lines: many PDFs have no
        // paragraph separators. Match one physical row rather than collecting
        // identically spelled words from unrelated parts of the page.
        if (lineWords.Length <= 16 && fontHeadings.TryGetValue(line, out var fontLevel)) return fontLevel;
        if (line.Any(char.IsLetter) && line.Where(char.IsLetter).All(char.IsUpper)) return 1;
        if (blankBefore && blankAfter && lineWords.Length <= 10
            && lineWords.All(word => word.Length > 0 && (char.IsUpper(word[0]) || word.All(char.IsLower))))
        {
            // Short isolated title/sentence-case lines are a text-only fallback.
            // Their words remain in the passage even if this heuristic is wrong.
            if (char.IsUpper(line[0])) return 1;
        }
        return 0;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^(#{1,6})\s+\S")] private static partial Regex MarkdownHeading();
    [GeneratedRegex(@"^((?:\d+|[A-Z])(?:\.\d+)*)[.)]?\s+\p{Lu}[^.!?;]{1,140}$")]
    private static partial Regex NumberedHeading();
    [GeneratedRegex(@"^(?:appendix|annex|annexe|chapter|part|schedule)\s+[\p{L}\d]+(?:\s*[:.\-–]\s*|\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AppendixHeading();
    [GeneratedRegex(@"^(?:[-*•]|\d+[.)])\s+")] private static partial Regex ListItem();
    [GeneratedRegex(@"^[=-]{3,}$")] private static partial Regex Setext();
    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?$")]
    private static partial Regex TableSeparator();
    private sealed record TableSpan(int Start, int End, DocumentBlock Block);
}
