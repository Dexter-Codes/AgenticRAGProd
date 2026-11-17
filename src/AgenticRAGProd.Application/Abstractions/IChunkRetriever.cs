using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

public interface IChunkRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        Guid documentId,
        string question,
        float[] questionVector,
        CancellationToken cancellationToken);
}