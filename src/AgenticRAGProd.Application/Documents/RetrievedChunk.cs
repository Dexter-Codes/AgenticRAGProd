namespace AgenticRAGProd.Application.Documents;

public sealed record RetrievedChunk(
    string Id,
    Guid DocumentId,
    string DocumentName,
    int PageNumber,
    string Content,
    bool HasUnprocessedImages,
    double Score)
{
    public ChunkContext? Context => ChunkContext.Read(Content);
}
