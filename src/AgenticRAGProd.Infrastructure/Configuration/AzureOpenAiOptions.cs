using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    [Required]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string ChatDeployment { get; init; } = string.Empty;

    [Required]
    public string EmbeddingDeployment { get; init; } = string.Empty;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 2048)]
    public int MaxOutputTokens { get; init; } = 500;
}