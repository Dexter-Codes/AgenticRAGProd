using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";

    [Required]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    [RegularExpression(@"^[a-z0-9][a-z0-9-]*$")]
    [StringLength(128, MinimumLength = 2)]
    public string IndexName { get; init; } = string.Empty;

    [Range(1, 4096)]
    public int EmbeddingDimensions { get; init; } = 1536;

    [Range(1, 1000)]
    public int CandidateCount { get; init; } = 20;

    [Range(1, 1000)]
    public int FinalContextCount { get; init; } = 5;
}
