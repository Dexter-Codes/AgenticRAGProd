using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Application.Conversations;
using AgenticRAGProd.Application.Documents;
using AgenticRAGProd.Infrastructure.Chunking;

namespace AgenticRAGProd.Tests.Chat;

internal sealed class ChatHarness
{
    public LocalCosmos Db { get; } = new();
    public Model Model { get; } = new();
    public Embedding Embeddings { get; } = new();
    public Retrieval Retrieval { get; } = new();
    public ChatOptions Options { get; init; } = new() { EnableQueryOptimization = false };
    public ChatAnswerService Answers => new(Model, Embeddings, Retrieval, new EmbeddingTokenCounter(), Microsoft.Extensions.Options.Options.Create(Options));
    public ChatService Service => new(Db.Store(), Answers, Microsoft.Extensions.Options.Options.Create(Options), Db.Clock);
}

internal sealed class Model : IAnswerGenerator
{
    public List<(string Instructions, string Input)> Calls { get; } = [];
    public Queue<string> Responses { get; } = new();
    public Func<CancellationToken, Task<string>>? Handler { get; set; }
    public Task<string> GenerateAsync(string instructions, string input, CancellationToken token)
    {
        lock (Calls) Calls.Add((instructions, input));
        return Handler is null ? Task.FromResult(Responses.Dequeue()) : Handler(token);
    }
}
internal sealed class Embedding : ITextEmbeddingService
{
    public List<string> Questions { get; } = [];
    public Task<float[]> GenerateAsync(string text, CancellationToken token)
    { lock (Questions) Questions.Add(text); return Task.FromResult(new float[] { 1, 0, 0 }); }
}
internal sealed class Retrieval : ICorpusChunkRetriever
{
    public List<string> Questions { get; } = [];
    public IReadOnlyList<RetrievedChunk> Chunks { get; set; } =
    [new("chunk-a", Guid.NewGuid(), "Technical guide", 2,
        new ChunkContext("Technical guide", 2, 3, ["Setup", "Sensor"], ["Uncertain layout"]).Prefix() + "Fresh sensor evidence.", false, .8)];
    public Task<IReadOnlyList<RetrievedChunk>> RetrieveCorpusAsync(string question, float[] vector, CancellationToken token)
    { lock (Questions) Questions.Add(question); return Task.FromResult(Chunks); }
}
