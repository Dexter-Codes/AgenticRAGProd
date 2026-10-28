namespace AgenticRAGProd.Application.Documents;

public sealed record IndexDocumentResult(
    Guid DocumentId,
    int PageCount,
    int IndexedChunks,
    int MaximumChunkTokens,
    IReadOnlyList<int> PagesWithUnprocessedImages)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
