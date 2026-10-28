using System.Text.RegularExpressions;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AgenticRAGProd.Infrastructure.Parsing;

internal sealed partial class PdfDocumentParser(IPageLayoutAnalyzer layoutAnalyzer) : IDocumentParser
{
    private const int MaximumPages = 100;
    private const double MarginFraction = 0.08;

    public IReadOnlyList<ExtractedPage> Parse(
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException(
                "The document stream must be readable.",
                nameof(content));
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var document = PdfDocument.Open(content);

        if (document.NumberOfPages > MaximumPages)
        {
            throw new InvalidOperationException(
                $"This parser supports up to {MaximumPages} pages.");
        }

        var rawPages = new List<RawPage>();
        var marginOccurrences = new Dictionary<string, int>(
            StringComparer.Ordinal);

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var words = page.GetWords().ToArray();

            var marginLines = words
                .Where(word =>
                    word.BoundingBox.Bottom >=
                        page.Height * (1 - MarginFraction)
                    || word.BoundingBox.Top <=
                        page.Height * MarginFraction)
                // Approximate shared baselines within a four-point band.
                .GroupBy(word =>
                    Math.Round(word.BoundingBox.Bottom / 4))
                .Select(group => string.Join(
                    " ",
                    group.OrderBy(word => word.BoundingBox.Left)
                        .Select(word => word.Text)))
                .Select(Normalize)
                .Where(line => line.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            // Count a candidate at most once per page.
            foreach (var line in marginLines)
            {
                marginOccurrences.TryGetValue(line, out var count);
                marginOccurrences[line] = count + 1;
            }

            var extractedWords = words
            .Select(word => new ExtractedWord(
                Text: word.Text,
                Left: word.BoundingBox.Left,
                Bottom: word.BoundingBox.Bottom,
                Right: word.BoundingBox.Right,
                Top: word.BoundingBox.Top,
                FontSize: word.Letters.Select(letter => letter.FontSize).DefaultIfEmpty().Average(),
                IsBold: word.Letters.Count > 0 && word.Letters.All(letter => letter.FontDetails.IsBold)))
            .ToArray();

            rawPages.Add(new RawPage(
                page.Number,
                ContentOrderTextExtractor.GetText(page),
                page.GetImages().Count(),
                marginLines,
                extractedWords));
        }

        var repetitionThreshold = Math.Max(
            3,
            (int)Math.Ceiling(rawPages.Count * 0.60));

        var repeatedMargins = marginOccurrences
            .Where(pair => pair.Value >= repetitionThreshold)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<ExtractedPage>(rawPages.Count);
        var fontSizes = rawPages.SelectMany(page => page.Words).Select(word => word.FontSize).ToArray();
        var structureExtractor = new DocumentStructureExtractor(layoutAnalyzer);

        foreach (var page in rawPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kept = new List<string>();
            var warnings = new List<string>();

            foreach (var rawLine in page.Text
                .Replace("\r\n", "\n")
                .Split('\n'))
            {
                var line = rawLine.Trim();
                var normalized = Normalize(line);

                if (normalized.Length > 0
                    && repeatedMargins.Contains(normalized)
                    && page.MarginLines.Contains(normalized))
                {
                    warnings.Add("Repeated margin text retained because header/footer classification is uncertain.");
                }
                kept.Add(line);
            }

            if (page.ImageCount > 0)
                warnings.Add("Page contains unprocessed images; OCR is not part of this pipeline.");
            if (string.IsNullOrWhiteSpace(page.Text))
                warnings.Add("No text extracted from page; OCR or a different parser may be required.");
            var text = string.Join("\n", kept).Trim();
            var structure = structureExtractor.Extract(text, page.Words, fontSizes);
            warnings.AddRange(structure.Warnings);
            result.Add(new ExtractedPage(
            PageNumber: page.PageNumber,
            Text: text,
            ImageCount: page.ImageCount,
            Words: page.Words)
            {
                // Keep repeated margin words searchable without allowing a
                // running title to reset the active section on every page.
                Blocks = structure.Blocks.Select(block =>
                    block.Kind == DocumentBlockKind.Heading
                    && repeatedMargins.Contains(Normalize(block.Text))
                    && page.MarginLines.Contains(Normalize(block.Text))
                        ? block with { Kind = DocumentBlockKind.Prose, HeadingLevel = 0 }
                        : block).ToArray(),
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
            });
        }

        return result;
    }

    private static string Normalize(string text)
    {
        // Normalize only the page-number label, not arbitrary numbers.
        var normalized = PageLabel().Replace(text, "PAGE #");

        // Allow PDF extraction to vary in whitespace between words.
        return Whitespace()
            .Replace(normalized, string.Empty)
            .ToUpperInvariant();
    }

    [GeneratedRegex(@"\bpage\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex PageLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record RawPage(
    int PageNumber,
    string Text,
    int ImageCount,
    HashSet<string> MarginLines,
    IReadOnlyList<ExtractedWord> Words);
}
