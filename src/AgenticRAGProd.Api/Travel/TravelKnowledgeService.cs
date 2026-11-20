using AgenticRAGProd.Application.Documents;

namespace AgenticRAGProd.Api.Travel;

public sealed record TravelKnowledgePassage(
    string ChunkId,
    Guid DocumentId,
    string DocumentName,
    int PageNumber,
    string Passage,
    bool IsTruncated,
    bool HasUnprocessedImages,
    ChunkContext? Context);

public sealed record TravelKnowledgeResponse(
    string Status,
    int DestinationId,
    IReadOnlyList<TravelKnowledgePassage> Passages);

public sealed class TravelKnowledgeService(
    DocumentRetrievalService retrieval,
    IConfiguration configuration,
    ILogger<TravelKnowledgeService> logger)
{
    public async Task<TravelKnowledgeResponse> SearchAsync(
        int destinationId,
        string? query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TravelKnowledgeResponse Empty(string status) =>
            new(status, destinationId, []);

        if (destinationId <= 0 ||
            string.IsNullOrWhiteSpace(query) ||
            query.Length > 500)
        {
            return Empty("invalid_request");
        }

        var setting = configuration[
            $"TravelKnowledge:Documents:{destinationId}"];

        if (!Guid.TryParse(setting, out var documentId) ||
            documentId == Guid.Empty)
        {
            return Empty("not_configured");
        }

        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            var chunks = await retrieval.RetrieveAsync(
                documentId, query.Trim(), timeout.Token);

            timeout.Token.ThrowIfCancellationRequested();

            var passages = chunks
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Content))
                .Take(5)
                .Select(chunk => new TravelKnowledgePassage(
                    chunk.Id,
                    chunk.DocumentId,
                    chunk.DocumentName,
                    chunk.PageNumber,
                    chunk.Content.Length > 3000
                        ? chunk.Content[..3000]
                        : chunk.Content,
                    chunk.Content.Length > 3000,
                    chunk.HasUnprocessedImages,
                    chunk.Context))
                .ToArray();

            return new TravelKnowledgeResponse(
                passages.Length > 0 ? "retrieved" : "no_evidence",
                destinationId,
                passages);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Empty("timed_out");
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogWarning(
                "Travel retrieval failed for {DestinationId}. Exception type {ExceptionType}",
                destinationId,
                exception.GetType().Name);

            return Empty("unavailable");
        }
    }
}