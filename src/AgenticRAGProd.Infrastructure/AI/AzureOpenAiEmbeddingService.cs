using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using AgenticRAGProd.Application.Abstractions;
using AgenticRAGProd.Infrastructure.Configuration;

namespace AgenticRAGProd.Infrastructure.AI;

internal sealed class AzureOpenAiEmbeddingService(
    EmbeddingClient client,
    IOptions<AzureOpenAiOptions> options)
    : ITextEmbeddingService
{
    private readonly AzureOpenAiOptions _options = options.Value;

    public async Task<float[]> GenerateAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        var response = await client.GenerateEmbeddingAsync(
            text,
            cancellationToken: timeout.Token);

        var vector = response.Value.ToFloats().ToArray();

        if (vector.Length == 0 ||
            vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException(
                "The embedding service returned an invalid vector.");
        }

        return vector;
    }
}