using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Infrastructure.Parsing;

namespace AgenticRAGProd.Infrastructure.Chunking;

internal sealed class TokenDocumentChunker(
    ITextTokenCounter tokenCounter,
    IOptions<ChunkingOptions> options) : IDocumentChunker
{
    private readonly ChunkingOptions _options = options.Value;

    public IReadOnlyList<DocumentChunk> Chunk(Guid documentId, string documentName, IReadOnlyList<ExtractedPage> pages)
    {
        if (documentId == Guid.Empty) throw new ArgumentException("Document ID cannot be empty.", nameof(documentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentNullException.ThrowIfNull(pages);
        if (_options.MaxTokens is < 128 or > 2048 || _options.OverlapTokens < 0
            || _options.OverlapTokens >= _options.MaxTokens / 2)
            throw new ArgumentException("Invalid chunk token or overlap budget.");
        if (pages.Any(page => page.PageNumber < 1)
            || !pages.Select(page => page.PageNumber).SequenceEqual(pages.Select(page => page.PageNumber).Distinct().Order()))
            throw new ArgumentException("Pages must have positive, unique, ascending numbers.", nameof(pages));

        var chunks = new List<DocumentChunk>();
        var path = new List<(int Level, string Title)>();
        var sources = new List<Source>();
        var sectionPath = Array.Empty<string>();
        var extractor = new DocumentStructureExtractor(new PageLayoutAnalyzer());
        var fontSizes = pages.SelectMany(page => page.Words ?? []).Select(word => word.FontSize).ToArray();
        foreach (var page in pages)
        {
            var extracted = page.Blocks is null ? extractor.Extract(page.Text, page.Words, fontSizes) : default;
            var blocks = page.Blocks ?? extracted.Blocks;
            var warnings = page.Warnings.Concat(extracted.Warnings ?? []).Distinct()
                .Select(warning => $"Page {page.PageNumber}: {warning}").ToArray();
            var firstBlock = true;
            foreach (var block in blocks)
            {
                if (block.Kind == DocumentBlockKind.Heading)
                {
                    var title = Regex.Replace(block.Text.Split('\n')[0], @"^#{1,6}\s+", "").Trim();
                    // Repeated section labels at page starts continue the section.
                    var repeatedPageHeading = firstBlock && path.Count > 0
                        && path.Contains((Math.Max(1, block.HeadingLevel), title));
                    if (!repeatedPageHeading)
                    {
                        AddSection();
                        path.RemoveAll(item => item.Level >= block.HeadingLevel);
                        path.Add((Math.Max(1, block.HeadingLevel), title));
                        sectionPath = path.Select(item => item.Title).ToArray();
                    }
                }
                sources.Add(new Source(block, page.PageNumber, page.ImageCount > 0, warnings));
                firstBlock = false;
            }
        }
        AddSection();
        return chunks;

        void AddSection()
        {
            if (sources.Count == 0) return;
            AddSectionChunks(documentId, documentName, sectionPath, sources, chunks);
            sources.Clear();
        }
    }

    private void AddSectionChunks(Guid id, string name, IReadOnlyList<string> path,
        List<Source> sources, List<DocumentChunk> chunks)
    {
        var warnings = sources.SelectMany(source => source.Warnings).Distinct().ToList();
        var current = new List<Piece>();
        string? tableHeader = null;
        ChunkContext Context(IEnumerable<Piece> pieces) => new(name,
            pieces.Min(piece => piece.Page), pieces.Max(piece => piece.Page), path, warnings.ToArray());
        string Body(IEnumerable<Piece> pieces) => (tableHeader is null ? "" : tableHeader + "\n")
            + string.Concat(pieces.Select(piece => piece.Text));
        bool Fits(IEnumerable<Piece> pieces) => tokenCounter.Count(Context(pieces).Prefix() + Body(pieces)) <= _options.MaxTokens;
        void Flush()
        {
            if (current.Count == 0) return;
            var context = Context(current);
            var content = context.Prefix() + Body(current);
            if (tokenCounter.Count(content) > _options.MaxTokens)
                throw new InvalidOperationException("Chunk metadata and content exceed the token budget.");
            chunks.Add(new DocumentChunk($"{id:N}-c{chunks.Count + 1:D6}", id, name,
                context.PageStart, content, current.Any(piece => piece.Images)));
            current.Clear();
        }
        void Add(Piece piece, bool overlap)
        {
            if (current.Count > 0 && !Fits(current.Append(piece)))
            {
                var tail = overlap ? Overlap(current) : [];
                Flush();
                if (tail.Count > 0 && Fits(tail.Append(piece))) current.AddRange(tail);
            }
            current.Add(piece);
        }
        foreach (var source in sources)
        {
            var block = source.Block;
            if (block.Kind == DocumentBlockKind.Table && block.TableHeader is not null && block.TableRows is { Count: > 0 })
            {
                Flush();
                tableHeader = block.TableHeader;
                var rows = block.TableRows.Select(row => new Piece(row + "\n", source.Page, source.Images)).ToArray();
                if (rows.All(row => Fits([row])))
                {
                    foreach (var row in rows) Add(row, overlap: false);
                    Flush();
                    tableHeader = null;
                    continue;
                }
                // A giant header or row cannot be kept atomic. Keep its source
                // text, without asserting that split fragments are complete cells.
                tableHeader = null;
                var warning = $"Page {source.Page}: Oversized table retained as text fragments; row/cell relationships may be incomplete.";
                if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
            }
            var text = block.Text + "\n\n";
            var prefix = new ChunkContext(name, source.Page, source.Page, path, warnings.ToArray()).Prefix();
            bool FitsText(string value) => tokenCounter.Count(prefix + value) <= _options.MaxTokens;
            if (!FitsText("x"))
                throw new InvalidOperationException("Document and section metadata leave no text budget. Increase Chunking:MaxTokens or shorten the document name/section labels.");
            // Reserve room for overlap when possible, but never at the cost of
            // dropping text when a large metadata prefix consumes that reserve.
            bool FitsSplit(string value) => tokenCounter.Count(prefix + value) <= _options.MaxTokens - _options.OverlapTokens;
            foreach (var part in Split(text, FitsSplit("x") ? FitsSplit : FitsText))
                Add(new Piece(part, source.Page, source.Images), overlap: true);
        }
        Flush();
    }

    private List<Piece> Overlap(List<Piece> pieces)
    {
        if (_options.OverlapTokens == 0) return [];
        var tail = new List<Piece>();
        for (var index = pieces.Count - 1; index >= 0; index--)
        {
            var piece = pieces[index];
            var after = string.Concat(tail.Select(item => item.Text));
            if (tokenCounter.Count(piece.Text + after) <= _options.OverlapTokens)
            {
                tail.Insert(0, piece);
                continue;
            }
            var boundaries = RuneBoundaries(piece.Text);
            var start = FindBoundary(boundaries.Count - 1,
                position => tokenCounter.Count(piece.Text[boundaries[boundaries.Count - 1 - position]..] + after) <= _options.OverlapTokens);
            var suffix = piece.Text[boundaries[boundaries.Count - 1 - start]..];
            if (suffix.Length > 0) tail.Insert(0, piece with { Text = suffix });
            break;
        }
        return tail;
    }

    // Preserve exact substrings while descending through paragraph, line,
    // sentence, word and finally Unicode scalar boundaries. No token decoding
    // or truncation can corrupt non-ASCII content.
    private static IEnumerable<string> Split(string text, Func<string, bool> fits, int level = 0)
    {
        if (fits(text)) { yield return text; yield break; }
        string[] separators = [@"\n\s*\n", @"\n", @"(?<=[.!?。！？])\s+", @"\s+"];
        if (level < separators.Length)
        {
            var offset = 0;
            foreach (Match match in Regex.Matches(text, separators[level]))
            {
                var end = match.Index + match.Length;
                foreach (var part in Split(text[offset..end], fits, level + 1)) yield return part;
                offset = end;
            }
            if (offset < text.Length)
                foreach (var part in Split(text[offset..], fits, level + 1)) yield return part;
            yield break;
        }
        var boundaries = RuneBoundaries(text);
        var start = 0;
        while (start < boundaries.Count - 1)
        {
            var length = FindBoundary(boundaries.Count - 1 - start,
                count => fits(text[boundaries[start]..boundaries[start + count]]));
            if (length == 0) throw new InvalidOperationException("The token budget cannot fit a source character after metadata.");
            yield return text[boundaries[start]..boundaries[start + length]];
            start += length;
        }
    }

    private static int FindBoundary(int maximum, Func<int, bool> fits)
    {
        var low = 0;
        var high = maximum;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (fits(middle)) low = middle; else high = middle - 1;
        }
        return low;
    }

    private static List<int> RuneBoundaries(string text)
    {
        var result = new List<int> { 0 };
        foreach (var rune in text.EnumerateRunes()) result.Add(result[^1] + rune.Utf16SequenceLength);
        return result;
    }

    private sealed record Source(DocumentBlock Block, int Page, bool Images, IReadOnlyList<string> Warnings);
    private sealed record Piece(string Text, int Page, bool Images);
}
