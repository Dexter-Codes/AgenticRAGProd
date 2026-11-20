using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgenticRAGProd.Api.Travel;

[McpServerToolType]
public sealed class TravelKnowledgeTools(TravelKnowledgeService search)
{
    [McpServerTool(
        Name = "search_travel_knowledge",
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description(
        "Retrieve passages with citations from the configured guide for a destination.")]
    public async Task<TravelKnowledgeResponse> SearchAsync(
        [Description("The confirmed destination ID.")] int destinationId,
        [Description("Search text, 1 to 500 characters.")] string query,
        CancellationToken cancellationToken)
    {
        var result = await search.SearchAsync(
            destinationId, query, cancellationToken);

        return result.Status switch
        {
            "retrieved" or "no_evidence" => result,
            "invalid_request" => throw new McpException(
                "Provide a positive destination ID and a query of 1–500 characters."),
            "not_configured" => throw new McpException(
                "No travel guide is configured for this destination."),
            "timed_out" => throw new McpException(
                "Travel knowledge retrieval timed out."),
            _ => throw new McpException(
                "Travel knowledge is temporarily unavailable.")
        };
    }
}