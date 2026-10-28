using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Parsing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class ParsingTests
{
    private static readonly DocumentStructureExtractor Extractor = new(new PageLayoutAnalyzer());
    private static ExtractedWord Word(string text, double x, double y, bool bold = false) =>
        new(text, x, y, x + text.Length * 4, y + 10, 10, bold);
    private static ExtractedWord[] TableWords(bool boldHeader = true) => [
        Word("Material", 10, 100, boldHeader), Word("Weight", 160, 100, boldHeader),
        Word("Copper", 10, 80), Word("Heavy", 160, 80),
        Word("Foam", 10, 60), Word("Light", 160, 60)];
    private const string TableText = "Material Weight\nCopper Heavy\nFoam Light";

    [Fact]
    public void AlignedRowsWithHeaderAndContiguousReadingOrderBecomeTable()
    {
        var result = Extractor.Extract("Introductory text.\n" + TableText + "\nEnd of text.", TableWords());
        var table = Assert.Single(result.Blocks.Where(block => block.Kind == DocumentBlockKind.Table));
        Assert.Equal("Material | Weight", table.TableHeader);
        Assert.Equal(new[] { "Copper | Heavy", "Foam | Light" }, table.TableRows);
        Assert.Equal(TableText, table.Text);
        Assert.Empty(result.Warnings);
        Assert.Equal("Introductory text.", result.Blocks.First().Text);
        Assert.Equal("End of text.", result.Blocks.Last().Text);
    }

    [Theory]
    [InlineData("no-header")]
    [InlineData("missing-cell")]
    [InlineData("shifted-column")]
    [InlineData("reading-order")]
    [InlineData("merged-cell")]
    [InlineData("duplicate-text")]
    public void AmbiguousTablesRetainOriginalContentWithoutInventingMappings(string kind)
    {
        var words = TableWords(kind != "no-header").ToList();
        var text = TableText;
        if (kind == "missing-cell") words.RemoveAt(3);
        if (kind == "shifted-column") words[3] = Word("Heavy", 190, 80);
        if (kind == "reading-order") text = "Material\nCopper\nFoam\nWeight\nHeavy\nLight";
        if (kind == "merged-cell") words[2] = Word(new string('x', 45), 10, 80);
        if (kind == "duplicate-text") text += "\n" + TableText;
        var result = Extractor.Extract(text, words);
        Assert.DoesNotContain(result.Blocks, block => block.Kind == DocumentBlockKind.Table);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(text, string.Join("\n", result.Blocks.Select(block => block.Text)));
    }

    [Fact]
    public void InconsistentPipeRowsRemainUnmappedAndAreWarned()
    {
        const string text = "| Name | Value |\n| --- | --- |\n| Incomplete |\n| A | B | C |";
        var result = Extractor.Extract(text);
        Assert.Equal(text, Assert.Single(result.Blocks).Text);
        Assert.DoesNotContain(result.Blocks, block => block.Kind == DocumentBlockKind.Table);
        Assert.Contains(result.Warnings, warning => warning.Contains("Inconsistent pipe table"));
    }

    [Fact]
    public void LayoutAnalysisUsesRelativeGeometryAndKeepsAllWords()
    {
        var words = TableWords();
        var rows = new PageLayoutAnalyzer().Analyze(words.Reverse().ToArray());
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(2, row.Cells.Count));
        Assert.Equal(new[] { "Material", "Weight", "Copper", "Heavy", "Foam", "Light" }, rows.SelectMany(row => row.Cells.Select(cell => cell.Text)));
    }

    [Fact]
    public void FontEvidenceFindsHeadingsWithoutBlankLines()
    {
        var words = new[]
        {
            Word("Preparation", 10, 100, true) with { FontSize = 18 },
            Word("Inspect", 10, 80), Word("the", 45, 80), Word("panel.", 65, 80),
            Word("Calibration", 10, 60, true) with { FontSize = 14 },
            Word("Adjust", 10, 40), Word("the", 40, 40), Word("sensor.", 60, 40)
        };
        var result = Extractor.Extract("Preparation\nInspect the panel.\nCalibration\nAdjust the sensor.", words);
        var headings = result.Blocks.Where(block => block.Kind == DocumentBlockKind.Heading).ToArray();
        Assert.Equal(new[] { "Preparation", "Calibration" }, headings.Select(block => block.Text));
        Assert.Equal(new[] { 1, 2 }, headings.Select(block => block.HeadingLevel));
    }

    [Fact]
    public void FontHierarchyIsStableWhenTheLargerHeadingIsOnAnotherPage()
    {
        double[] documentSizes = [18, 14, 10, 10, 10, 10, 10, 10];
        var result = Extractor.Extract("Calibration\nAdjust the sensor.", [
            Word("Calibration", 10, 100, true) with { FontSize = 14 },
            Word("Adjust", 10, 80), Word("the", 45, 80), Word("sensor.", 65, 80)], documentSizes);
        Assert.Equal(2, result.Blocks.First().HeadingLevel);
    }

    [Fact]
    public void PdfFontAndGeometryEvidenceReachIngestionBlocks()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(600, 800);
        page.AddText("Material", 10, new PdfPoint(40, 700), bold);
        page.AddText("Weight", 10, new PdfPoint(240, 700), bold);
        page.AddText("Copper", 10, new PdfPoint(40, 680), font);
        page.AddText("Heavy", 10, new PdfPoint(240, 680), font);
        page.AddText("Foam", 10, new PdfPoint(40, 660), font);
        page.AddText("Light", 10, new PdfPoint(240, 660), font);
        using var stream = new MemoryStream(builder.Build());
        var extracted = Assert.Single(new PdfDocumentParser(new PageLayoutAnalyzer()).Parse(stream, CancellationToken.None));
        Assert.True(extracted.Blocks!.Any(block => block.Kind == DocumentBlockKind.Table), System.Text.Json.JsonSerializer.Serialize(extracted));
        var table = Assert.Single(extracted.Blocks!.Where(block => block.Kind == DocumentBlockKind.Table));
        Assert.Equal("Material | Weight", table.TableHeader);
        Assert.Equal(new[] { "Copper | Heavy", "Foam | Light" }, table.TableRows);
        Assert.Empty(extracted.Warnings);
    }

    [Fact]
    public void ActualPdfParsingPreservesRepeatedMarginsAndCrossPageText()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        for (var i = 0; i < 3; i++)
        {
            var page = builder.AddPage(600, 800);
            page.AddText("IMPORTANT REPEATED TEXT", 10, new PdfPoint(40, 770), bold);
            if (i == 0) page.AddText("# Preparation", 16, new PdfPoint(40, 700), bold);
            page.AddText($"Unique body passage {i} continues.", 10, new PdfPoint(40, 660), font);
        }
        using var stream = new MemoryStream(builder.Build());
        var pages = new PdfDocumentParser(new PageLayoutAnalyzer()).Parse(stream, CancellationToken.None);
        Assert.Equal(3, pages.Count);
        Assert.All(pages, page =>
        {
            Assert.Contains("IMPORTANT REPEATED TEXT", page.Text);
            Assert.Contains(page.Warnings, warning => warning.Contains("Repeated margin text retained"));
            Assert.NotEmpty(page.Blocks!);
            Assert.DoesNotContain(page.Blocks!, block => block.Kind == DocumentBlockKind.Heading && block.Text == "IMPORTANT REPEATED TEXT");
            Assert.NotEmpty(page.Words!);
        });
        Assert.Contains("Unique body passage 2", pages[2].Text);
    }

    [Fact]
    public void EmptyPageProducesWarningAndCancellationIsHonored()
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(600, 800);
        using var stream = new MemoryStream(builder.Build());
        var parser = new PdfDocumentParser(new PageLayoutAnalyzer());
        Assert.Contains(Assert.Single(parser.Parse(stream, CancellationToken.None)).Warnings, warning => warning.Contains("No text extracted"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => parser.Parse(stream, cancelled.Token));
    }
}
