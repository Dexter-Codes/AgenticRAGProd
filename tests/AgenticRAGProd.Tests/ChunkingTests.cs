using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Chunking;
using AgenticRAGProd.Infrastructure.Configuration;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class ChunkingTests
{
    private static readonly EmbeddingTokenCounter Tokens = new();
    private static IReadOnlyList<DocumentChunk> Chunk(string text, int budget = 256, int overlap = 0) =>
        Create(budget, overlap).Chunk(Guid.NewGuid(), "Field guide.pdf", [new(1, text, 0)]);
    private static TokenDocumentChunker Create(int budget = 256, int overlap = 0) =>
        new(Tokens, Options.Create(new ChunkingOptions { MaxTokens = budget, OverlapTokens = overlap }));
    internal static string Body(DocumentChunk chunk) => chunk.Content[(chunk.Content.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public void HeadingsBuildPathsAndAppendicesResetThem()
    {
        var chunks = Chunk("# Maintenance\nIntroductory material.\n## Inspection\nInspect the panel.\nAppendix B: Equipment\nB.1 Tools\nUse a torque wrench.\nB.2 Storage\nKeep tools dry.");
        Assert.Equal(new[] { "Maintenance", "Inspection" }, chunks.Single(c => Body(c).Contains("Inspect the panel")).Context!.SectionPath);
        Assert.Equal(new[] { "Appendix B: Equipment", "B.1 Tools" }, chunks.Single(c => Body(c).Contains("torque wrench")).Context!.SectionPath);
        Assert.DoesNotContain(chunks.Single(c => Body(c).Contains("Keep tools dry")).Context!.SectionPath, s => s.Contains("Maintenance"));
    }

    [Theory]
    [InlineData("Overview\n\nA useful paragraph.", "Overview")]
    [InlineData("Executive summary\n\nA useful paragraph.", "Executive summary")]
    [InlineData("SAFETY NOTES\nRead before operating.", "SAFETY NOTES")]
    [InlineData("Assembly\n========\nAttach the base.", "Assembly")]
    [InlineData("3 Installation\nFit the frame.\n3.2 Calibration\nAdjust the sensor.", "3.2 Calibration")]
    [InlineData("Annex C – References\nReference material.", "Annex C – References")]
    public void RecognizesDifferentHeadingForms(string text, string heading)
    {
        var chunks = Chunk(text);
        Assert.Contains(chunks, chunk => chunk.Context!.SectionPath.Contains(heading));
        Assert.Equal(Normalize(text), Normalize(string.Concat(chunks.Select(Body))));
    }

    [Fact]
    public void HeadingOnlyAndPreambleContentAreNotDropped()
    {
        var chunks = Chunk("A preamble with punctuation.\n\n# First\n## Empty subsection\n# Last");
        Assert.Equal("A preamble with punctuation. # First ## Empty subsection # Last", Normalize(string.Concat(chunks.Select(Body))));
        Assert.Empty(chunks[0].Context!.SectionPath);
    }

    [Fact]
    public void ListsAndOrdinaryProseAreNotTreatedAsHeadings()
    {
        var chunks = Chunk("1. Tighten the screws.\n2. Check the seal.\n\nThis is ordinary prose.\nMore prose follows.");
        Assert.All(chunks, chunk => Assert.Empty(chunk.Context!.SectionPath));
        Assert.Contains("2. Check the seal.", string.Concat(chunks.Select(Body)));
    }

    [Fact]
    public void SectionAndImageProvenanceContinueAcrossPages()
    {
        var chunks = Create().Chunk(Guid.NewGuid(), "Manual.pdf", [
            new(4, "# Operations\nThe process begins here and", 0),
            new(5, "continues on this page.\nMore details follow.", 1)]);
        var chunk = Assert.Single(chunks);
        Assert.Equal(4, chunk.PageNumber);
        Assert.Equal(4, chunk.Context!.PageStart);
        Assert.Equal(5, chunk.Context.PageEnd);
        Assert.Equal(new[] { "Operations" }, chunk.Context.SectionPath);
        Assert.True(chunk.HasUnprocessedImages);
        Assert.Contains("continues on this page", Body(chunk));
    }

    [Fact]
    public void RepeatedPageHeadingContinuesTheSameSection()
    {
        var chunks = Create().Chunk(Guid.NewGuid(), "Manual.pdf", [new(1, "# Setup\nStart here.", 0), new(2, "# Setup\nThen continue.", 0)]);
        Assert.Equal(2, Assert.Single(chunks).Context!.PageEnd);
        Assert.Contains("Then continue.", Body(chunks[0]));
    }

    [Fact]
    public void RepeatedAncestorHeadingDoesNotEraseTheActiveSubsection()
    {
        var chunks = Create().Chunk(Guid.NewGuid(), "Manual.pdf", [
            new(1, "# Setup\n## Sensors\nStart here.", 0), new(2, "# Setup\nThen continue.", 0)]);
        var continuation = chunks.Single(chunk => Body(chunk).Contains("Then continue."));
        Assert.Equal(new[] { "Setup", "Sensors" }, continuation.Context!.SectionPath);
        Assert.Equal(1, continuation.Context.PageStart);
        Assert.Equal(2, continuation.Context.PageEnd);
    }

    [Fact]
    public void NewSectionDoesNotReceivePreviousSectionOverlap()
    {
        var text = "# First\n" + string.Join(" ", Enumerable.Range(0, 160).Select(i => $"first{i}"))
            + "\n# Second\nA separate section.";
        var chunks = Chunk(text, 128, 20);
        var second = Assert.Single(chunks.Where(chunk => chunk.Context!.SectionPath.SequenceEqual(new[] { "Second" })));
        Assert.Equal("# Second A separate section.", Normalize(Body(second)));
    }

    [Fact]
    public void ChunkPageRangesAndImageFlagsFollowSourcePiecesWhenPagesSplit()
    {
        var text1 = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"left{i}"));
        var text2 = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"right{i}"));
        var chunks = Create(160, 20).Chunk(Guid.NewGuid(), "Manual.pdf", [
            new(7, "# Readings\n" + text1, 0), new(8, text2, 1)]);
        Assert.Contains(chunks, chunk => chunk.Context!.PageEnd == 7 && !chunk.HasUnprocessedImages);
        Assert.Contains(chunks, chunk => chunk.Context!.PageStart == 8 && chunk.HasUnprocessedImages);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("Readings", Assert.Single(chunk.Context!.SectionPath));
            if (Body(chunk).Contains("right", StringComparison.Ordinal))
            {
                Assert.Equal(8, chunk.Context.PageEnd);
                Assert.True(chunk.HasUnprocessedImages);
            }
            Assert.True(Tokens.Count(chunk.Content) <= 160);
        });
    }

    [Theory]
    [InlineData("sentence")]
    [InlineData("words")]
    [InlineData("unicode")]
    [InlineData("unbroken")]
    public void RecursiveFallbackPreservesContentAndFullTokenBudget(string kind)
    {
        var text = kind switch
        {
            "sentence" => string.Concat(Enumerable.Range(0, 170).Select(i => $"Observation {i} was recorded. ")),
            "words" => string.Join(" ", Enumerable.Range(0, 500).Select(i => $"sample{i}")),
            "unicode" => string.Concat(Enumerable.Repeat("界🙂 café नमस्ते ", 180)),
            _ => string.Concat(Enumerable.Repeat("x7z9🙂", 400))
        };
        var chunks = Chunk(text, 128);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(Tokens.Count(chunk.Content), 1, 128));
        Assert.Equal(Normalize(text), Normalize(string.Concat(chunks.Select(Body))));
        Assert.DoesNotContain('\uFFFD', string.Concat(chunks.Select(Body)));
        Assert.Equal(chunks.Count, chunks.Select(chunk => chunk.Id).Distinct().Count());
    }

    [Fact]
    public void PrefixAndSectionContextConsumeTheBudget()
    {
        var text = "# Instrumentation reference\n" + string.Join(" ", Enumerable.Range(0, 400).Select(i => $"reading{i}"));
        var chunks = Chunk(text, 128, 16);
        Assert.All(chunks, chunk =>
        {
            Assert.True(Tokens.Count(chunk.Content) <= 128);
            Assert.True(Tokens.Count(chunk.Content) > Tokens.Count(Body(chunk)));
            Assert.Equal("Field guide.pdf", chunk.Context!.Document);
            Assert.Contains("Instrumentation reference", chunk.Context.SectionPath);
        });
    }

    [Fact]
    public void OverlapIsBoundedAndDoesNotLoseOrReorderContent()
    {
        var text = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"measurement{i:D4}"));
        var chunks = Chunk(text, 128, 20);
        var bodies = chunks.Select(Body).ToArray();
        var reconstructed = bodies[0];
        var overlappingBoundaries = 0;
        for (var i = 1; i < bodies.Length; i++)
        {
            var overlap = Enumerable.Range(0, Math.Min(bodies[i - 1].Length, bodies[i].Length) + 1)
                .Where(length => bodies[i - 1].EndsWith(bodies[i][..length], StringComparison.Ordinal)).Max();
            if (overlap > 0) overlappingBoundaries++;
            Assert.True(Tokens.Count(bodies[i][..overlap]) <= 20);
            reconstructed += bodies[i][overlap..];
        }
        Assert.True(overlappingBoundaries > 0);
        Assert.Equal(Normalize(text), Normalize(reconstructed));
    }

    [Fact]
    public void OversizedTableDegradesToPreservedTextAndWarning()
    {
        var text = "| Feature | Description |\n| --- | --- |\n| Probe | " + new string('q', 2000) + " |";
        var chunks = Chunk(text, 256);
        Assert.All(chunks, chunk => Assert.True(Tokens.Count(chunk.Content) <= 256));
        Assert.Equal(Normalize(text), Normalize(string.Concat(chunks.Select(Body))));
        Assert.Contains(chunks.SelectMany(chunk => chunk.Context!.Warnings), warning => warning.Contains("Oversized table"));
    }

    [Fact]
    public void TableGroupsRepeatHeadersAndRetainEveryRow()
    {
        var rows = Enumerable.Range(0, 50).Select(i => $"| Item{i:D3} | {i * 7} units | required | optional |").ToArray();
        var header = "| Item | Quantity | Status | Note |\n| --- | --- | --- | --- |";
        var chunks = Chunk(header + "\n" + string.Join("\n", rows), 160);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.StartsWith(header, Body(chunk));
            Assert.True(Tokens.Count(chunk.Content) <= 160);
        });
        Assert.Equal(rows, chunks.SelectMany(chunk => Body(chunk).Split('\n').Skip(2)).Where(line => line.Length > 0));
    }

    [Fact]
    public void MetadataThatCannotFitFailsClearlyWithoutTruncatingSource()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Create(128).Chunk(Guid.NewGuid(),
            string.Concat(Enumerable.Repeat("多文書", 300)), [new(1, "Retain this content.", 0)]));
        Assert.Contains("metadata", error.Message);
    }

    [Fact]
    public void ContextRoundTripsEscapedLabelsAndLegacyContentRemainsUsable()
    {
        var context = new ChunkContext("A\n\"quoted\".pdf", 2, 8, ["Part \\\"A\""], ["Uncertain layout"]);
        Assert.Equal(context.Document, ChunkContext.Read(context.Prefix() + "Body")!.Document);
        Assert.Null(ChunkContext.Read("Legacy passage."));
        Assert.Null(ChunkContext.Read("RAG context: invalid\n\nBody"));
    }

    [Fact]
    public void RejectsInvalidPageOrderAndOverlap()
    {
        Assert.Throws<ArgumentException>(() => Create().Chunk(Guid.NewGuid(), "Doc", [new(2, "x", 0), new(1, "y", 0)]));
        Assert.Throws<ArgumentException>(() => Create(128, 64).Chunk(Guid.NewGuid(), "Doc", [new(1, "x", 0)]));
    }
}
