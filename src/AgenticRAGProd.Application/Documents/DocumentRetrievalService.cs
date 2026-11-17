using AgenticRAGProd.Application.Abstractions;

namespace AgenticRAGProd.Application.Documents;

public sealed class DocumentRetrievalService(
    ITextEmbeddingService embeddingService,
    IChunkRetriever retriever)
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        Guid documentId,
        string question,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document ID cannot be empty.",
                nameof(documentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        if (question.Length > 2000)
        {
            throw new ArgumentException(
                "Questions must not exceed 2,000 characters.",
                nameof(question));
        }

        var normalizedQuestion = question.Trim();

        var vector = await embeddingService.GenerateAsync(
            normalizedQuestion,
            cancellationToken);

        var chunks = await retriever.RetrieveAsync(
            documentId,
            normalizedQuestion,
            vector,
            cancellationToken);

        if (chunks.Any(chunk => chunk.DocumentId != documentId))
        {
            throw new InvalidOperationException(
                "Retrieved evidence exceeded the requested document scope.");
        }

        return chunks;
    }
}