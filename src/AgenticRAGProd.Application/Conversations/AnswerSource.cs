using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Conversations;

public sealed record AnswerSource(
    int Number,
    string ChunkId,
    string DocumentName,
    int PageNumber,
    string Passage,
    bool HasUnprocessedImages)
{
    public Guid DocumentId { get; init; }
    public ChunkContext? Context => ChunkContext.Read(Passage);
}
