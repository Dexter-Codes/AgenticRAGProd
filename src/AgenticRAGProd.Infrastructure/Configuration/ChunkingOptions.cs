using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    [Range(128, 2048)]
    public int MaxTokens { get; init; } = 512;

    [Range(0, 256)]
    public int OverlapTokens { get; init; } = 64;
}