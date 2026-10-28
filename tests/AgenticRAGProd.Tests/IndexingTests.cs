using Microsoft.Extensions.Options;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Tests.Chat;
using AgenticRAGProd.Infrastructure.Chunking;
using AgenticRAGProd.Infrastructure.Configuration;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class IndexingTests
{
    [Fact]
    public async Task IndexingPropagatesWarningsIncludesMetadataInEmbeddingsAndReplacesOldChunks()
    {
        var id = Guid.NewGuid();
        var pages = new ExtractedPage[]
        {
            new(1, "# Overview\nContent begins here.", 0) { Warnings = ["Uncertain layout on page 1"] },
            new(2, "It continues on the next page.", 0),
            new(3, "", 1) { Warnings = ["No text extracted from page 3"] }
        };
        var embeddings = new LocalEmbedding();
        var writer = new RecordingWriter();
        var counter = new EmbeddingTokenCounter();
        var chunker = new TokenDocumentChunker(counter, Options.Create(new ChunkingOptions()));
        var service = new DocumentIndexingService(new LocalStorage(), new LocalParser(pages), chunker, counter, embeddings, writer);
        var result = await service.IndexAsync(id, "Operations.pdf", CancellationToken.None);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(new[] { 3 }, result.PagesWithUnprocessedImages);
        Assert.Contains("Page 1: Uncertain layout on page 1", result.Warnings);
        Assert.Contains("Page 3: No text extracted from page 3", result.Warnings);
        Assert.Equal(embeddings.Texts.Count, result.IndexedChunks);
        Assert.Equal(embeddings.Texts.Max(counter.Count), result.MaximumChunkTokens);
        Assert.All(embeddings.Texts, text => Assert.StartsWith("RAG context: ", text));
        Assert.Equal(embeddings.Texts, writer.Chunks.Select(item => item.Chunk.Content));
        Assert.Equal(writer.Chunks.Select(item => item.Chunk.Id), writer.Retained);
        Assert.Equal(new[] { "upsert", "cleanup" }, writer.Operations);
    }

    [Fact]
    public async Task AllChunksAreValidatedBeforeAnyEmbeddingIsRequested()
    {
        var embeddings = new LocalEmbedding();
        var writer = new RecordingWriter();
        var counter = new EmbeddingTokenCounter();
        var service = new DocumentIndexingService(new LocalStorage(), new LocalParser([new(1, "Content", 0)]),
            new InvalidChunker(), counter, embeddings, writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IndexAsync(Guid.NewGuid(), "Doc", CancellationToken.None));
        Assert.Empty(embeddings.Texts);
        Assert.Empty(writer.Operations);
    }

    [Fact]
    public async Task EmptyExtractionDoesNotEmbedOrErasePreviouslyIndexedDocument()
    {
        var embeddings = new LocalEmbedding();
        var writer = new RecordingWriter();
        var counter = new EmbeddingTokenCounter();
        var service = new DocumentIndexingService(new LocalStorage(), new LocalParser([new(1, "", 1)]),
            new TokenDocumentChunker(counter, Options.Create(new ChunkingOptions())), counter, embeddings, writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IndexAsync(Guid.NewGuid(), "Doc", CancellationToken.None));
        Assert.Empty(embeddings.Texts);
        Assert.Empty(writer.Operations);
    }

    [Fact]
    public async Task AnswerSourcesExposePageRangeAndKeepExistingCitationFields()
    {
        var id = Guid.NewGuid();
        var context = new ChunkContext("Guide", 3, 5, ["Preparation"], ["Uncertain layout"]);
        var h = new ChatHarness();
        h.Retrieval.Chunks = [new RetrievedChunk("passage", id, "Guide", 3, context.Prefix() + "Source content.", false, .8)];
        h.Model.Responses.Enqueue("See [1].");
        var answer = await h.Service.SendAsync(null, "Where is the source?", CancellationToken.None);
        var source = Assert.Single(answer.Sources);
        Assert.Equal(1, source.Number);
        Assert.Equal(3, source.PageNumber);
        Assert.Equal(5, source.Context!.PageEnd);
        Assert.Contains("Uncertain layout", h.Model.Calls.Single().Input);
        Assert.Contains("Source content.", h.Model.Calls.Single().Input);
    }

    private sealed class LocalStorage : IDocumentStorage
    {
        public Task UploadAsync(Guid id, Stream content, CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(Guid id, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream());
    }
    private sealed class LocalParser(IReadOnlyList<ExtractedPage> pages) : IDocumentParser
    {
        public IReadOnlyList<ExtractedPage> Parse(Stream stream, CancellationToken token) => pages;
    }
    private sealed class LocalEmbedding : ITextEmbeddingService
    {
        public List<string> Texts { get; } = [];
        public Task<float[]> GenerateAsync(string text, CancellationToken token) { Texts.Add(text); return Task.FromResult(new float[] { 1, 0 }); }
    }
    private sealed class RecordingWriter : IChunkIndexWriter
    {
        public List<string> Operations { get; } = [];
        public IReadOnlyList<EmbeddedChunk> Chunks { get; private set; } = [];
        public IReadOnlyCollection<string> Retained { get; private set; } = [];
        public Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken token) { Operations.Add("upsert"); Chunks = chunks; return Task.CompletedTask; }
        public Task RemoveObsoleteAsync(Guid id, IReadOnlyCollection<string> retained, CancellationToken token) { Operations.Add("cleanup"); Retained = retained; return Task.CompletedTask; }
    }
    private sealed class InvalidChunker : IDocumentChunker
    {
        public IReadOnlyList<DocumentChunk> Chunk(Guid id, string name, IReadOnlyList<ExtractedPage> pages) =>
            [new("small", id, name, 1, "Valid", false), new("large", id, name, 1, string.Concat(Enumerable.Repeat("large-token ", 4000)), false)];
    }
}
