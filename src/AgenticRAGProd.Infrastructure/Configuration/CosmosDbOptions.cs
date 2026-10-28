using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class CosmosDbOptions
{
    public const string SectionName = "CosmosDb";

    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    [Required]
    public string DatabaseName { get; init; } = "rag-demo";

    [Required]
    public string ContainerName { get; init; } = "chat";

    [Range(5, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;
}
