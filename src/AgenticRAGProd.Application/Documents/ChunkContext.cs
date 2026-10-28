using System.Text.Json;

namespace AgenticRAGProd.Application.Documents;

// Stored inside the existing content field: no Azure index schema change.
// The same prefix is embedded, searched, returned, and counted in the budget.
public sealed record ChunkContext(
    string Document,
    int PageStart,
    int PageEnd,
    IReadOnlyList<string> SectionPath,
    IReadOnlyList<string> Warnings)
{
    private const string Marker = "RAG context: ";

    public string Prefix() => Marker + JsonSerializer.Serialize(this) + "\n\n";

    public static ChunkContext? Read(string content)
    {
        if (!content.StartsWith(Marker, StringComparison.Ordinal))
            return null; // Passages from the previous ingestion format.
        var end = content.IndexOf('\n');
        if (end < 0) return null;
        try
        {
            var value = JsonSerializer.Deserialize<ChunkContext>(content[Marker.Length..end]);
            return value is { Document: not null, SectionPath: not null, Warnings: not null }
                && value.PageStart > 0 && value.PageEnd >= value.PageStart ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
