using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class AzureStorageOptions
{
    public const string SectionName = "AzureStorage";

    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    [Required]
    [RegularExpression(
        @"^[a-z0-9]+(-[a-z0-9]+)*$",
        ErrorMessage = "Use a valid lowercase container name.")]
    [StringLength(63, MinimumLength = 3)]
    public string ContainerName { get; init; } = string.Empty;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 60;
}