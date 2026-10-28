namespace AgenticRAGProd.Application.Documents;

public sealed record ExtractedPage(
    int PageNumber,
    string Text,
    int ImageCount,
    IReadOnlyList<ExtractedWord>? Words = null)
{
    public IReadOnlyList<DocumentBlock>? Blocks { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
