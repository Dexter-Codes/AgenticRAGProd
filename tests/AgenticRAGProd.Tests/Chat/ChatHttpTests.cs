using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using AgenticRAGProd.Api;
using AgenticRAGProd.Application;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Infrastructure.Chunking;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatHttpTests
{
    private const string Chat = "/api/chat";

    [Fact]
    public async Task HttpChatRestoresHistoryAndProcessesRepeatedQuestionsAfterHostRecreation()
    {
        using var h = new ChatHarness();
        Guid id;
        string messageId;
        await using (var app = App(h))
        {
            h.Model.Responses.Enqueue("Initial response [1].");
            var first = await Request(app, Chat, "POST", query: "Initial question");
            Assert.Equal(200, first.Status);
            using var json = JsonDocument.Parse(first.Body);
            id = json.RootElement.GetProperty("conversationId").GetGuid();
            var firstMessage = json.RootElement.GetProperty("messageId").GetString();
            Assert.Single(json.RootElement.GetProperty("sources").EnumerateArray());
            h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Model.Responses.Enqueue("{\"question\":\"Standalone follow-up question\",\"clarification\":null}");
            h.Model.Responses.Enqueue("Follow-up response [1].");
            var next = await Request(app, Chat, "POST", id, "What about that?");
            Assert.Equal(200, next.Status);
            using var nextJson = JsonDocument.Parse(next.Body);
            messageId = nextJson.RootElement.GetProperty("messageId").GetString()!;
            Assert.Equal(id, nextJson.RootElement.GetProperty("conversationId").GetGuid());
            Assert.NotEqual(firstMessage, messageId);
            Assert.Single(nextJson.RootElement.GetProperty("sources").EnumerateArray());
        }
        await using var restarted = App(h);
        Assert.Equal(2, h.Db.Messages.Count);
        Assert.All(h.Db.Messages, message => Assert.False(message.TryGetProperty("sourcesId", out _)));

        h.Db.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.Responses.Enqueue("{\"question\":\"Standalone follow-up question\",\"clarification\":null}");
        h.Model.Responses.Enqueue("Newly generated response [1].");
        var repeated = await Request(restarted, Chat, "POST", id, "What about that?");
        Assert.Equal(200, repeated.Status);
        using var repeatedJson = JsonDocument.Parse(repeated.Body);
        Assert.Equal(id, repeatedJson.RootElement.GetProperty("conversationId").GetGuid());
        Assert.NotEqual(messageId, repeatedJson.RootElement.GetProperty("messageId").GetString());
        Assert.Equal("Newly generated response [1].", repeatedJson.RootElement.GetProperty("answer").GetString());
        Assert.Single(repeatedJson.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal(5, h.Model.Calls.Count);
        using var preparation = JsonDocument.Parse(h.Model.Calls[3].Input);
        var history = preparation.RootElement.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("Initial question", history[0].GetProperty("User").GetString());
        Assert.Equal("Initial response [1].", history[0].GetProperty("Assistant").GetString());
        Assert.Equal("What about that?", history[1].GetProperty("User").GetString());
        Assert.Equal("Follow-up response [1].", history[1].GetProperty("Assistant").GetString());
        Assert.Equal(3, h.Embeddings.Questions.Count);
        Assert.Equal(3, h.Db.Messages.Count);
    }

    [Theory]
    [InlineData("answered")]
    [InlineData("clarification")]
    [InlineData("insufficient_evidence")]
    public async Task HttpExposesActualAnswerKindWithoutChangingStoredMessages(string kind)
    {
        using var h = new ChatHarness { Options = new ChatOptions { EnableQueryOptimization = kind == "clarification" } };
        await using var app = App(h);
        if (kind == "clarification")
            h.Model.Responses.Enqueue("{\"question\":null,\"retrievalQuestion\":null,\"clarification\":\"Which document do you mean?\"}");
        else h.Model.Responses.Enqueue(kind == "answered" ? "Supported answer [1]."
            : AgenticRAGProd.Application.Prompts.PromptConstants.InsufficientEvidenceAnswer);
        var response = await Request(app, Chat, "POST", query: kind == "clarification" ? "What about that?" : "Question");
        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(kind, json.RootElement.GetProperty("answerKind").GetString());
        Assert.Equal(kind == "clarification" ? 0 : 1, json.RootElement.GetProperty("sources").GetArrayLength());
        var stored = Assert.Single(h.Db.Messages);
        Assert.Equal(6, stored.EnumerateObject().Count());
        Assert.False(stored.TryGetProperty("answerKind", out _));
    }

    [Fact]
    public async Task OptimizedChatUsesRewrittenSearchAndPersistsOriginalQuestion()
    {
        const string original = "What do the sensor setup instructions say about calibration?";
        const string retrieval = "sensor setup calibration instructions";
        using var h = new ChatHarness { Options = new ChatOptions { EnableQueryOptimization = true } };
        await using var app = App(h);
        h.Model.Responses.Enqueue(JsonSerializer.Serialize(new
        {
            question = original,
            retrievalQuestion = retrieval,
            clarification = (string?)null
        }));
        h.Model.Responses.Enqueue("Calibrate the sensor as instructed [1].");

        var response = await Request(app, Chat, "POST", query: original);

        Assert.Equal(200, response.Status);
        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Equal(retrieval, Assert.Single(h.Embeddings.Questions));
        Assert.Equal(retrieval, Assert.Single(h.Retrieval.Questions));
        var stored = Assert.Single(h.Db.Messages);
        Assert.Equal(original, stored.GetProperty("query").GetString());
        Assert.False(stored.TryGetProperty("retrievalQuestion", out _));
        using var json = JsonDocument.Parse(response.Body);
        Assert.Single(json.RootElement.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task RepeatedInitialQuestionsCreateSeparateConversations()
    {
        using var h = new ChatHarness();
        await using var app = App(h);
        h.Model.Responses.Enqueue("Generated response [1].");
        h.Model.Responses.Enqueue("Generated response [1].");
        var first = await Request(app, Chat, "POST", query: "Question");
        var second = await Request(app, Chat, "POST", query: "Question");
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        using var firstJson = JsonDocument.Parse(first.Body);
        using var secondJson = JsonDocument.Parse(second.Body);
        Assert.NotEqual(firstJson.RootElement.GetProperty("conversationId").GetGuid(),
            secondJson.RootElement.GetProperty("conversationId").GetGuid());
        Assert.NotEqual(firstJson.RootElement.GetProperty("messageId").GetString(),
            secondJson.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(2, h.Model.Calls.Count);
        Assert.Equal(2, h.Db.Messages.Count);
        Assert.Single(firstJson.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Single(secondJson.RootElement.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task HttpContractRejectsUnknownConversationAndInvalidInput()
    {
        using var h = new ChatHarness();
        await using var app = App(h);
        Assert.Equal(404, (await Request(app, Chat, "POST", Guid.NewGuid(), "Question")).Status);
        Assert.Equal(400, (await Request(app, Chat, "POST", query: " ")).Status);
        Assert.Equal(400, (await Request(app, Chat, "POST", Guid.Empty, "Question")).Status);
        Assert.Equal(400, (await Request(app, Chat, "POST", query: new string('x', 2001))).Status);
        Assert.Empty(h.Model.Calls);
        h.Model.Responses.Enqueue("Generated response [1].");
        Assert.Equal(200, (await Request(app, Chat, "POST", query: "Question")).Status);
    }

    [Fact]
    public async Task HttpErrorsNeverExposeProviderExceptionTextOrCreateFalseAnswers()
    {
        using var h = new ChatHarness();
        await using var app = App(h);
        h.Model.Handler = _ => throw new ArgumentException("SYNTHETIC_PRIVATE_PROVIDER_DETAIL");
        var failure = await Request(app, Chat, "POST", query: "Question");
        Assert.Equal(503, failure.Status);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", failure.Body);
        Assert.DoesNotContain("insufficient", failure.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidGeneratedAnswerReturns502WithoutSavingAnExchange()
    {
        using var h = new ChatHarness();
        await using var app = App(h);
        h.Model.Responses.Enqueue("An answer without required source citations.");

        var failure = await Request(app, Chat, "POST", query: "Question");

        Assert.Equal(502, failure.Status);
        using var body = JsonDocument.Parse(failure.Body);
        Assert.Equal("Generation failed", body.RootElement.GetProperty("title").GetString());
        Assert.Empty(h.Db.Messages);
    }

    [Fact]
    public async Task InternalCancellationReturns504WithoutSavingAnExchange()
    {
        using var h = new ChatHarness();
        await using var app = App(h);
        h.Model.Handler = _ => throw new OperationCanceledException();

        var failure = await Request(app, Chat, "POST", query: "Question");

        Assert.Equal(504, failure.Status);
        using var body = JsonDocument.Parse(failure.Body);
        Assert.Equal("Chat operation timed out", body.RootElement.GetProperty("title").GetString());
        Assert.Empty(h.Db.Messages);
    }

    [Fact]
    public async Task ModelAuthenticationFailureLogsStatusWithoutProviderDetails()
    {
        using var h = new ChatHarness();
        var logs = new RecordedLogs();
        await using var app = App(h, logs);
        var response = new Mock<System.ClientModel.Primitives.PipelineResponse>();
        response.SetupGet(value => value.Status).Returns(401);
        h.Model.Handler = _ => throw new System.ClientModel.ClientResultException(
            "SYNTHETIC_PRIVATE_PROVIDER_DETAIL", response.Object, null);
        var failure = await Request(app, Chat, "POST", query: "Question");
        Assert.Equal(503, failure.Status);
        var log = Assert.Single(logs.Messages.Where(message => message.StartsWith("Chat operation failed.", StringComparison.Ordinal)));
        Assert.Contains("401", log);
        Assert.Contains("ClientResultException", log);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", log);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", failure.Body);
    }

    [Fact]
    public async Task StorageSetupErrorsAreActionableAndCorrelatedWithoutProviderDetails()
    {
        using var h = new ChatHarness();
        var logs = new RecordedLogs();
        await using var app = App(h, logs);
        const string setupMessage = "Verify Sqlite:DatabasePath points to a writable local database file.";
        h.Model.Handler = _ => throw new ChatStorageException(setupMessage,
            new Microsoft.Data.Sqlite.SqliteException("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", 14));
        using var activity = new Activity("chat-storage-failure").SetIdFormat(ActivityIdFormat.W3C).Start();
        var failure = await Request(app, Chat, "POST", query: "Question");
        Assert.Equal(503, failure.Status);
        using var body = JsonDocument.Parse(failure.Body);
        Assert.Equal("Chat storage configuration error", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(setupMessage, body.RootElement.GetProperty("detail").GetString());
        Assert.Equal(activity.Id, body.RootElement.GetProperty("traceId").GetString());
        var log = Assert.Single(logs.Messages.Where(message => message.StartsWith("Chat operation failed.", StringComparison.Ordinal)));
        Assert.Contains(activity.Id!, log);
        Assert.Contains("SqliteException", log);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", log);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_PROVIDER_DETAIL", failure.Body);
    }

    private static WebApplication App(ChatHarness h, ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:LocalSingleUserMode"] = "true" });
        builder.Services.AddLogging();
        if (logs is not null) builder.Logging.AddProvider(logs);
        builder.Services.AddProblemDetails();
        builder.Services.AddRouting();
        builder.Services.AddApiDocumentation();
        var server = new Mock<IServer>();
        server.SetupGet(s => s.Features).Returns(new FeatureCollection());
        builder.Services.AddSingleton(server.Object);
        builder.Services.AddApplication();
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(h.Options));
        builder.Services.AddSingleton<TimeProvider>(h.Db.Clock);
        builder.Services.AddSingleton<IConversationStore>(h.Db.Store());
        builder.Services.AddSingleton<IAnswerGenerator>(h.Model);
        builder.Services.AddSingleton<ITextEmbeddingService>(h.Embeddings);
        builder.Services.AddSingleton<ICorpusChunkRetriever>(h.Retrieval);
        builder.Services.AddSingleton<ITextTokenCounter>(new EmbeddingTokenCounter());
        return global::Program.CreateApp(builder);
    }

    private sealed class RecordedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(Messages);
        public void Dispose() { }

        private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    // Execute actual Minimal API handlers/filters without opening a port,
    // loading appsettings or secrets, or constructing Azure clients.
    private static async Task<(int Status, string Body)> Request(WebApplication app, string route,
        string method, Guid? id = null, string? query = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText!.TrimEnd('/') == route.TrimEnd('/')
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.SetEndpoint(endpoint);
        context.Request.Method = method;
        if (query is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { conversationId = id, query }));
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            var body = new Mock<IHttpRequestBodyDetectionFeature>();
            body.SetupGet(b => b.CanHaveBody).Returns(true);
            context.Features.Set(body.Object);
        }
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
