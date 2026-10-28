namespace AgenticRAGProd.Application.Abstractions;

public interface ITextEmbeddingService
{
    Task<float[]> GenerateAsync(
        string text,
        CancellationToken cancellationToken);
}