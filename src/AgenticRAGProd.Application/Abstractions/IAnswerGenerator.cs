namespace AgenticRAGProd.Application.Abstractions;

public interface IAnswerGenerator
{
    Task<string> GenerateAsync(
        string instructions,
        string input,
        CancellationToken cancellationToken);
}