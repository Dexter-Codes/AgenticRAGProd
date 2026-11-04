using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Infrastructure.Configuration;

public sealed class SqliteOptions
{
    public const string SectionName = "Sqlite";

    [Required]
    public string DatabasePath { get; init; } = "App_Data/chat.db";

    [Range(1, 120)]
    public int CommandTimeoutSeconds { get; init; } = 30;
}
