namespace AgenticRAGProd.Application.Documents;

public sealed record DocumentChunk(
    string Id,
    Guid DocumentId,
    string DocumentName,
    int PageNumber,
    string Content,
    bool HasUnprocessedImages)
{
    public ChunkContext? Context => ChunkContext.Read(Content);
}
