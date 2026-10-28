using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.Search;

internal sealed class AzureSearchChunkIndexWriter(
    SearchClient client,
    IOptions<AzureSearchOptions> options)
    : IChunkIndexWriter
{
    public async Task UpsertAsync(
        IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
            return;

        var dimensions = options.Value.EmbeddingDimensions;

        // Validate every vector before uploading any records.
        foreach (var item in chunks)
        {
            if (item.Vector.Length != dimensions ||
                item.Vector.Any(value => !float.IsFinite(value)))
            {
                throw new InvalidOperationException(
                    $"Chunk {item.Chunk.Id} has an invalid embedding.");
            }
        }

        foreach (var batch in chunks.Chunk(100))
        {
            var documents = batch.Select(item => new SearchDocument
            {
                ["id"] = item.Chunk.Id,
                ["documentId"] = item.Chunk.DocumentId.ToString("N"),
                ["documentName"] = item.Chunk.DocumentName,
                ["pageNumber"] = item.Chunk.PageNumber,
                ["content"] = item.Chunk.Content,
                ["hasUnprocessedImages"] =
                    item.Chunk.HasUnprocessedImages,
                ["contentVector"] = item.Vector
            }).ToArray();

            await client.UploadDocumentsAsync(
                documents,
                new IndexDocumentsOptions
                {
                    ThrowOnAnyError = true
                },
                cancellationToken);
        }
    }

    public async Task RemoveObsoleteAsync(
        Guid documentId,
        IReadOnlyCollection<string> retainedChunkIds,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document ID cannot be empty.",
                nameof(documentId));
        }

        ArgumentNullException.ThrowIfNull(retainedChunkIds);

        // Empty ingestion must not accidentally erase the document.
        if (retainedChunkIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one current chunk must be retained.",
                nameof(retainedChunkIds));
        }

        var retained = retainedChunkIds.ToHashSet(StringComparer.Ordinal);
        var documentKey = documentId.ToString("N");
        var searchOptions = new SearchOptions
        {
            Filter = SearchFilter.Create($"documentId eq {documentKey}")
        };
        searchOptions.Select.Add("id");

        var response = await client.SearchAsync<SearchDocument>(
            "*", searchOptions, cancellationToken);
        var obsoleteIds = new List<string>();

        // Finish enumeration before changing the result set.
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var id = (string)result.Document["id"];
            if (!retained.Contains(id)) obsoleteIds.Add(id);
        }

        foreach (var batch in obsoleteIds.Chunk(100))
        {
            await client.DeleteDocumentsAsync(
                "id", batch, new IndexDocumentsOptions { ThrowOnAnyError = true }, cancellationToken);
        }
    }
}
