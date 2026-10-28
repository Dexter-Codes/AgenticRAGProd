using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Infrastructure.Parsing;

internal sealed class PageLayoutAnalyzer : IPageLayoutAnalyzer
{
    public IReadOnlyList<LayoutRow> Analyze(
        IReadOnlyList<ExtractedWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var usableWords = words
            .Where(word =>
                !string.IsNullOrWhiteSpace(word.Text)
                && word.Right > word.Left
                && word.Top > word.Bottom)
            .ToArray();

        if (usableWords.Length == 0)
            return Array.Empty<LayoutRow>();

        var heights = usableWords
            .Select(word => word.Top - word.Bottom)
            .OrderBy(height => height)
            .ToArray();

        var medianHeight = heights[heights.Length / 2];

        // Relative to font size rather than fixed PDF coordinates.
        var rowTolerance = Math.Max(1.5, medianHeight * 0.35);
        var cellGapThreshold = Math.Max(10, medianHeight * 1.5);

        var physicalRows = new List<WordRow>();

        foreach (var word in usableWords
            .OrderByDescending(word => word.Bottom)
            .ThenBy(word => word.Left))
        {
            var row = physicalRows
                .Where(candidate =>
                    Math.Abs(candidate.Baseline - word.Bottom)
                        <= rowTolerance)
                .OrderBy(candidate =>
                    Math.Abs(candidate.Baseline - word.Bottom))
                .FirstOrDefault();

            if (row is null)
            {
                row = new WordRow(word.Bottom);
                physicalRows.Add(row);
            }

            row.Words.Add(word);
        }

        return physicalRows
            .OrderByDescending(row => row.Baseline)
            .Select(row => new LayoutRow(
                Bottom: row.Baseline,
                Cells: SplitCells(
                    row.Words,
                    cellGapThreshold)))
            .ToArray();
    }

    private static IReadOnlyList<LayoutCell> SplitCells(
        IReadOnlyList<ExtractedWord> words,
        double gapThreshold)
    {
        var ordered = words
            .OrderBy(word => word.Left)
            .ToArray();

        var cells = new List<LayoutCell>();
        var currentWords = new List<string>();

        var left = ordered[0].Left;
        var right = ordered[0].Right;

        currentWords.Add(ordered[0].Text);

        foreach (var word in ordered.Skip(1))
        {
            var gap = word.Left - right;

            if (gap > gapThreshold)
            {
                cells.Add(new LayoutCell(
                    string.Join(" ", currentWords),
                    left,
                    right));

                currentWords.Clear();
                left = word.Left;
            }

            currentWords.Add(word.Text);
            right = Math.Max(right, word.Right);
        }

        cells.Add(new LayoutCell(
            string.Join(" ", currentWords),
            left,
            right));

        return cells;
    }

    private sealed class WordRow(double baseline)
    {
        public double Baseline { get; } = baseline;

        public List<ExtractedWord> Words { get; } = new();
    }
}