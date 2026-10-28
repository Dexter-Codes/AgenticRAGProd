using Microsoft.ML.Tokenizers;
using AgenticRAGProd.Application.Abstractions;

namespace AgenticRAGProd.Infrastructure.Chunking;

internal sealed class EmbeddingTokenCounter : ITextTokenCounter
{
    private readonly TiktokenTokenizer _tokenizer =
        TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.CountTokens(text);
    }
}