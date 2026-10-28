using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Application.Abstractions;

// Deliberately distinct from the mandatory-document retrieval contract.
// Only the Development/local-single-user chat routes expose this scope.
public interface ICorpusChunkRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveCorpusAsync(string question, float[] vector, CancellationToken cancellationToken);
}
