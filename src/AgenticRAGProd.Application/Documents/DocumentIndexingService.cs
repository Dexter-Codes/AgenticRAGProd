using AgenticRAGProd.Application.Abstractions;

namespace AgenticRAGProd.Application.Documents;

public sealed class DocumentIndexingService(
    IDocumentStorage storage,
    IDocumentParser parser,
    IDocumentChunker chunker,
    ITextTokenCounter tokenCounter,
    ITextEmbeddingService embeddingService,
    IChunkIndexWriter indexWriter)
{
    // Baseline retrieval budget, deliberately below the model limit.
    private const int MaximumTokensPerChunk = 2048;

    // Bound synchronous work for this assessment implementation.
    private const int MaximumPages = 100;

    public async Task<IndexDocumentResult> IndexAsync(
        Guid documentId,
        string documentName,
        CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Document ID cannot be empty.", nameof(documentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

        IReadOnlyList<ExtractedPage> pages;

        await using (var content = await storage.OpenReadAsync(
            documentId,
            cancellationToken))
        {
            pages = parser.Parse(content, cancellationToken);
        }

        if (pages.Count > MaximumPages)
        {
            throw new InvalidOperationException(
                $"This synchronous workflow supports up to {MaximumPages} pages.");
        }

        var chunks = chunker.Chunk(
            documentId,
            documentName,
            pages);

        if (chunks.Count == 0)
        {
            throw new InvalidOperationException(
                "No searchable text was extracted. OCR may be required.");
        }

        var maximumChunkTokens = 0;

        // Check all chunks before spending on embeddings.
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = tokenCounter.Count(chunk.Content);

            if (count > MaximumTokensPerChunk)
            {
                throw new InvalidOperationException(
                    $"Page {chunk.PageNumber} contains {count} tokens. " +
                    "It requires smaller, structure-aware chunks.");
            }

            maximumChunkTokens = Math.Max(
                maximumChunkTokens, count);
        }

        var embeddedChunks = new List<EmbeddedChunk>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var vector = await embeddingService.GenerateAsync(
                chunk.Content,
                cancellationToken);

            embeddedChunks.Add(new EmbeddedChunk(chunk, vector));
        }

        await indexWriter.UpsertAsync(
            embeddedChunks,
            cancellationToken);

        var retainedChunkIds = embeddedChunks
            .Select(item => item.Chunk.Id)
            .ToArray();

        await indexWriter.RemoveObsoleteAsync(
            documentId,
            retainedChunkIds,
            cancellationToken);

        return new IndexDocumentResult(
            DocumentId: documentId,
            PageCount: pages.Count,
            IndexedChunks: embeddedChunks.Count,
            MaximumChunkTokens: maximumChunkTokens,
            PagesWithUnprocessedImages: pages
                .Where(page => page.ImageCount > 0)
                .Select(page => page.PageNumber)
                .ToArray())
        {
            Warnings = pages.SelectMany(page => page.Warnings.Select(warning => $"Page {page.PageNumber}: {warning}"))
                .Concat(chunks.SelectMany(chunk => chunk.Context?.Warnings ?? []))
                .Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}
