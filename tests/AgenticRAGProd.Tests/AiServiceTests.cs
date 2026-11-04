using System.Net;
using System.Text;
using System.Text.Json;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using AgenticRAGProd.Infrastructure.AI;
using AgenticRAGProd.Infrastructure.Configuration;
using Xunit;

namespace AgenticRAGProd.Tests;

// Exercise the real SDK adapters without contacting a model endpoint.
public sealed class AiServiceTests
{
    private static readonly IOptions<AzureOpenAiOptions> Settings = Options.Create(new AzureOpenAiOptions());

    [Fact]
    public async Task EmbeddingAdapterSendsInputAndReturnsTheProviderVector()
    {
        using var handler = new ModelTransport("""
            {"object":"list","model":"embedding-model","data":[{"object":"embedding","index":0,"embedding":[1,0.5,0]}],"usage":{"prompt_tokens":3,"total_tokens":3}}
            """);
        var service = new AzureOpenAiEmbeddingService(Client(handler).GetEmbeddingClient("embeddings"), Settings);
        var vector = await service.GenerateAsync("Fresh retrieval question", default);
        Assert.Equal(new float[] { 1, .5f, 0 }, vector);
        Assert.Contains("/deployments/embeddings/embeddings", handler.Uri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Fresh retrieval question", body.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task AnswerAdapterSendsSystemAndEvidenceMessagesAndReturnsModelText()
    {
        using var handler = new ModelTransport(ChatResponse("Supported answer [1]."));
        var builder = Kernel.CreateBuilder();
        builder.AddAzureOpenAIChatCompletion("chat", Client(handler));
        var service = new SemanticKernelAnswerGenerator(builder.Build(), Settings);
        var answer = await service.GenerateAsync("Use supplied evidence.", "Evidence passage [1].", default);
        Assert.Equal("Supported answer [1].", answer);
        Assert.Contains("/deployments/chat/chat/completions", handler.Uri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Use supplied evidence.", messages[0].GetProperty("content").GetString());
        Assert.Equal("Evidence passage [1].", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task EmptyModelOutputIsRejected()
    {
        using var handler = new ModelTransport(ChatResponse(""));
        var builder = Kernel.CreateBuilder();
        builder.AddAzureOpenAIChatCompletion("chat", Client(handler));
        var service = new SemanticKernelAnswerGenerator(builder.Build(), Settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("Use evidence.", "Passage", default));
    }

    private static AzureOpenAIClient Client(ModelTransport handler) => new(
        new Uri("https://local.invalid"), new AzureKeyCredential("synthetic-key"),
        new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) });

    private static string ChatResponse(string text) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test", @object = "chat.completion", created = 1, model = "chat-model",
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } },
        usage = new { prompt_tokens = 5, completion_tokens = 5, total_tokens = 10 }
    });

    private sealed class ModelTransport(string response) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
