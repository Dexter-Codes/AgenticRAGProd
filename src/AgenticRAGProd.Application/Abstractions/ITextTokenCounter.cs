namespace AgenticRAGProd.Application.Abstractions;

public interface ITextTokenCounter
{
    int Count(string text);
}