using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using AgenticRAGProd.Api;
using AgenticRAGProd.Application;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Chunking;
using AgenticRAGProd.Infrastructure.Configuration;
using AgenticRAGProd.Tests.Chat;
using Xunit;

namespace AgenticRAGProd.Tests;

public sealed class DocumentHttpTests
{
    private const string UploadRoute = "/api/documents";
    private const string IndexRoute = "/api/documents/{documentId:guid}/index";
    private const string SourceText = "The sensor must be calibrated every 30 days.";

    [Fact]
    public async Task UploadedDocumentIsIndexedAndBecomesChatEvidence()
    {
        var chat = new ChatHarness { Options = new ChatOptions() };
        var storage = new MemoryStorage();
        var parser = new RecordingParser();
        var index = new MemoryIndex();
        await using var app = App(chat, storage, parser, index);
        var uploadedBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSYNTHETIC_UPLOAD_CONTENT");

        var upload = await Upload(app, "Sensor Guide.PDF", uploadedBytes);

        Assert.Equal(200, upload.Status);
        using var uploaded = JsonDocument.Parse(upload.Body);
        var documentId = uploaded.RootElement.GetProperty("documentId").GetGuid();
        Assert.NotEqual(Guid.Empty, documentId);
        Assert.Equal("Sensor Guide.PDF", uploaded.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(uploadedBytes.Length, uploaded.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("Uploaded", uploaded.RootElement.GetProperty("status").GetString());
        Assert.Equal(uploadedBytes, storage.Files[documentId]);
        Assert.Empty(index.Chunks);
        Assert.Empty(chat.Embeddings.Questions);

        var indexed = await Send(app, IndexRoute, documentId: documentId, documentName: "Sensor Guide.PDF");

        Assert.Equal(200, indexed.Status);
        using var result = JsonDocument.Parse(indexed.Body);
        Assert.Equal(documentId, result.RootElement.GetProperty("documentId").GetGuid());
        Assert.Equal(1, result.RootElement.GetProperty("pageCount").GetInt32());
        Assert.Equal(index.Chunks.Count, result.RootElement.GetProperty("indexedChunks").GetInt32());
        Assert.Equal(uploadedBytes, Assert.Single(parser.Inputs));
        Assert.Equal(new[] { "upsert", "cleanup" }, index.Operations);
        Assert.All(index.Chunks, item => Assert.Equal(documentId, item.Chunk.DocumentId));
        Assert.Contains(SourceText, Assert.Single(chat.Embeddings.Questions));

        const string question = "When must the sensor be calibrated?";
        chat.Model.Responses.Enqueue(JsonSerializer.Serialize(new
        {
            question,
            retrievalQuestion = "sensor calibration interval",
            clarification = (string?)null
        }));
        chat.Model.Responses.Enqueue("Calibrate it every 30 days [1].");
        var answer = await Send(app, "/api/chat", body: Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { conversationId = (Guid?)null, query = question })), contentType: "application/json");

        Assert.Equal(200, answer.Status);
        using var response = JsonDocument.Parse(answer.Body);
        Assert.Equal("Calibrate it every 30 days [1].", response.RootElement.GetProperty("answer").GetString());
        var source = Assert.Single(response.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal(documentId, source.GetProperty("documentId").GetGuid());
        Assert.Contains(SourceText, source.GetProperty("passage").GetString());
        Assert.Equal("sensor calibration interval", chat.Embeddings.Questions.Last());
        Assert.Equal(question, Assert.Single(chat.Db.Messages).GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("notes.txt", 10)]
    [InlineData("empty.pdf", 0)]
    [InlineData("oversized.pdf", 10 * 1024 * 1024 + 1)]
    public async Task InvalidUploadIsRejectedBeforeStorageOrIndexing(string filename, int length)
    {
        var chat = new ChatHarness();
        var storage = new MemoryStorage();
        var parser = new RecordingParser();
        var index = new MemoryIndex();
        await using var app = App(chat, storage, parser, index);

        var response = await Upload(app, filename, new byte[length]);

        Assert.Equal(400, response.Status);
        Assert.Empty(storage.Files);
        Assert.Equal(0, storage.ReadCalls);
        Assert.Empty(parser.Inputs);
        Assert.Empty(chat.Embeddings.Questions);
        Assert.Empty(index.Operations);
    }

    [Theory]
    [InlineData(true, "Guide.pdf")]
    [InlineData(false, " ")]
    [InlineData(false, null)]
    public async Task InvalidIndexingInputIsRejectedBeforeReadingOrEmbedding(bool emptyId, string? name)
    {
        var chat = new ChatHarness();
        var storage = new MemoryStorage();
        var parser = new RecordingParser();
        var index = new MemoryIndex();
        await using var app = App(chat, storage, parser, index);

        var response = await Send(app, IndexRoute, documentId: emptyId ? Guid.Empty : Guid.NewGuid(), documentName: name);

        Assert.Equal(400, response.Status);
        Assert.Equal(0, storage.ReadCalls);
        Assert.Empty(parser.Inputs);
        Assert.Empty(chat.Embeddings.Questions);
        Assert.Empty(index.Operations);
    }

    [Fact]
    public async Task OversizedDocumentNameIsRejectedBeforeReadingOrEmbedding()
    {
        var chat = new ChatHarness();
        var storage = new MemoryStorage();
        var parser = new RecordingParser();
        var index = new MemoryIndex();
        await using var app = App(chat, storage, parser, index);

        var response = await Send(app, IndexRoute, documentId: Guid.NewGuid(), documentName: new string('x', 256));

        Assert.Equal(400, response.Status);
        Assert.Equal(0, storage.ReadCalls);
        Assert.Empty(chat.Embeddings.Questions);
        Assert.Empty(index.Operations);
    }

    private static WebApplication App(ChatHarness chat, MemoryStorage storage, RecordingParser parser, MemoryIndex index)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:LocalSingleUserMode"] = "true" });
        builder.Services.AddLogging();
        builder.Services.AddProblemDetails();
        builder.Services.AddRouting();
        builder.Services.AddApiDocumentation();
        builder.Services.Configure<FormOptions>(options => options.MemoryBufferThreshold = 16 * 1024 * 1024);
        var server = new Mock<IServer>();
        server.SetupGet(item => item.Features).Returns(new FeatureCollection());
        builder.Services.AddSingleton(server.Object);
        builder.Services.AddApplication();
        builder.Services.AddSingleton(Options.Create(chat.Options));
        builder.Services.AddSingleton<TimeProvider>(chat.Db.Clock);
        builder.Services.AddSingleton<IConversationStore>(chat.Db.Store());
        builder.Services.AddSingleton<IAnswerGenerator>(chat.Model);
        builder.Services.AddSingleton<ITextEmbeddingService>(chat.Embeddings);
        builder.Services.AddSingleton<IDocumentStorage>(storage);
        builder.Services.AddSingleton<IDocumentParser>(parser);
        builder.Services.AddSingleton<IChunkIndexWriter>(index);
        builder.Services.AddSingleton<ICorpusChunkRetriever>(index);
        var counter = new EmbeddingTokenCounter();
        builder.Services.AddSingleton<ITextTokenCounter>(counter);
        builder.Services.AddSingleton<IDocumentChunker>(new TokenDocumentChunker(counter, Options.Create(new ChunkingOptions())));
        var app = global::Program.CreateApp(builder);
        // Run the selected endpoint through the host's exception middleware too.
        ((IApplicationBuilder)app).Run(context => context.GetEndpoint()!.RequestDelegate!(context));
        return app;
    }

    private static async Task<(int Status, string Body)> Upload(WebApplication app, string filename, byte[] bytes)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(bytes), "file", filename);
        return await Send(app, UploadRoute, body: await multipart.ReadAsByteArrayAsync(),
            contentType: multipart.Headers.ContentType!.ToString());
    }

    private static async Task<(int Status, string Body)> Send(WebApplication app, string route,
        Guid? documentId = null, string? documentName = null, byte[]? body = null, string? contentType = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == route);
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.Path = documentId.HasValue ? $"/api/documents/{documentId}/index" : route;
        if (documentId.HasValue) context.Request.RouteValues["documentId"] = documentId.Value.ToString();
        if (documentName is not null) context.Request.QueryString = new QueryString("?documentName=" + Uri.EscapeDataString(documentName));
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
            context.Request.ContentType = contentType;
            var detection = new Mock<IHttpRequestBodyDetectionFeature>();
            detection.SetupGet(item => item.CanHaveBody).Returns(true);
            context.Features.Set(detection.Object);
        }
        context.Response.Body = new MemoryStream();
        await ((IApplicationBuilder)app).Build()(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private sealed class MemoryStorage : IDocumentStorage
    {
        public Dictionary<Guid, byte[]> Files { get; } = [];
        public int ReadCalls { get; private set; }
        public async Task UploadAsync(Guid documentId, Stream content, CancellationToken token)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, token);
            Files.Add(documentId, copy.ToArray());
        }
        public Task<Stream> OpenReadAsync(Guid documentId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReadCalls++;
            return Task.FromResult<Stream>(new MemoryStream(Files[documentId]));
        }
    }

    // Parsing quality has separate tests; this stub records exactly what indexing read from storage.
    private sealed class RecordingParser : IDocumentParser
    {
        public List<byte[]> Inputs { get; } = [];
        public IReadOnlyList<ExtractedPage> Parse(Stream content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            Inputs.Add(copy.ToArray());
            return [new ExtractedPage(1, SourceText, 0)];
        }
    }

    private sealed class MemoryIndex : IChunkIndexWriter, ICorpusChunkRetriever
    {
        public List<EmbeddedChunk> Chunks { get; } = [];
        public List<string> Operations { get; } = [];
        public Task UpsertAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Operations.Add("upsert");
            Chunks.AddRange(chunks);
            return Task.CompletedTask;
        }
        public Task RemoveObsoleteAsync(Guid documentId, IReadOnlyCollection<string> retainedIds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Operations.Add("cleanup");
            Chunks.RemoveAll(item => item.Chunk.DocumentId == documentId && !retainedIds.Contains(item.Chunk.Id));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RetrievedChunk>> RetrieveCorpusAsync(string question, float[] vector, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Chunks.Select(item => new RetrievedChunk(item.Chunk.Id,
                item.Chunk.DocumentId, item.Chunk.DocumentName, item.Chunk.PageNumber, item.Chunk.Content,
                item.Chunk.HasUnprocessedImages, 1)).ToArray());
        }
    }
}
