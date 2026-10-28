using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.Search;

internal sealed class AzureSearchChunkRetriever(
    SearchClient client,
    IOptions<AzureSearchOptions> options)
    : ICorpusChunkRetriever
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveCorpusAsync(string question,
        float[] questionVector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(questionVector);

        if (options.Value.CandidateCount is < 1 or > 1000
            || options.Value.FinalContextCount < 1
            || options.Value.FinalContextCount > options.Value.CandidateCount)
            throw new ArgumentException("Invalid search candidate or final context count.");

        if (questionVector.Length !=
                options.Value.EmbeddingDimensions ||
            questionVector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException(
                "The query vector has invalid dimensions or values.",
                nameof(questionVector));
        }

        var vectorQuery = new VectorizedQuery(questionVector)
        {
            KNearestNeighborsCount = options.Value.CandidateCount
        };

        vectorQuery.Fields.Add("contentVector");

        var searchOptions = new SearchOptions
        {
            Size = options.Value.CandidateCount,
            QueryType = SearchQueryType.Simple,
            VectorSearch = new VectorSearchOptions()
        };

        searchOptions.VectorSearch.Queries.Add(vectorQuery);

        // Search the passage text, not filenames.
        searchOptions.SearchFields.Add("content");

        foreach (var field in new[]
        {
            "id",
            "documentId",
            "documentName",
            "pageNumber",
            "content",
            "hasUnprocessedImages"
        })
        {
            searchOptions.Select.Add(field);
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var response = await client.SearchAsync<SearchDocument>(
            question,
            searchOptions,
            timeout.Token);

        var chunks = new List<RetrievedChunk>();

        await foreach (var result in response.Value
            .GetResultsAsync()
            .WithCancellation(timeout.Token))
        {
            var document = result.Document;

            chunks.Add(new RetrievedChunk(
                Id: (string)document["id"],
                DocumentId: Guid.Parse(
                    (string)document["documentId"]),
                DocumentName: (string)document["documentName"],
                PageNumber: Convert.ToInt32(
                    document["pageNumber"]),
                Content: (string)document["content"],
                HasUnprocessedImages: Convert.ToBoolean(
                    document["hasUnprocessedImages"]),
                Score: result.Score ?? 0));

            if (chunks.Count == options.Value.FinalContextCount)
                break;
        }

        return chunks;
    }
}
