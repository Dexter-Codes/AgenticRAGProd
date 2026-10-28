using System.ComponentModel.DataAnnotations;

namespace AgenticRAGProd.Application.Conversations;

public sealed class ChatOptions
{
    public bool LocalSingleUserMode { get; init; }
    public bool EnableQueryOptimization { get; init; } = true;
    [Range(1, 30)] public int HistoryTurns { get; init; } = 8;
    [Range(128, 8192)] public int HistoryTokens { get; init; } = 2048;
    [Range(512, 24000)] public int EvidenceTokens { get; init; } = 6000;
    [Range(10, 240)] public int RequestTimeoutSeconds { get; init; } = 180;
}
