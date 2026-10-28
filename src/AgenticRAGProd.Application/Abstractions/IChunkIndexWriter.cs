using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

public interface IChunkIndexWriter
{
    Task UpsertAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken);

    Task RemoveObsoleteAsync(
    Guid documentId,
    IReadOnlyCollection<string> retainedChunkIds,
    CancellationToken cancellationToken);
}