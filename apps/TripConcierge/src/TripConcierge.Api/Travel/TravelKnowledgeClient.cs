using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace TripConcierge.Api.Travel;

public sealed record TravelPassage(
    string ChunkId,
    Guid DocumentId,
    string DocumentName,
    int PageNumber,
    string Passage,
    bool IsTruncated,
    bool HasUnprocessedImages,
    JsonElement? Context);

public sealed record TravelKnowledgeLookup(
    string Status,
    int DestinationId,
    IReadOnlyList<TravelPassage> Passages);

public sealed class TravelKnowledgeClient(
    HttpClient httpClient,
    ILogger<TravelKnowledgeClient> logger)
{

    // Fixed configured upstream only; no browser-controlled URLs or tool names.
    public Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken token) =>
        httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

    public async Task<object> InspectMcpAsync(int? destinationId, string? query, CancellationToken token)
    {
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        }, httpClient);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: token);
        if (destinationId is null)
        {
            var tools = await client.ListToolsAsync(cancellationToken: token);
            return tools.Select(tool => new { tool.Name, tool.Description, schema = tool.JsonSchema }).ToArray();
        }
        return await client.CallToolAsync("search_travel_knowledge",
            new Dictionary<string, object?> { ["destinationId"] = destinationId, ["query"] = query },
            cancellationToken: token);
    }

  public async Task<TravelKnowledgeLookup> SearchAsync(
    DestinationCandidate destination,
    CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    TravelKnowledgeLookup Empty(string status) =>
        new(status, destination.Id, []);

    using var timeout = CancellationTokenSource
        .CreateLinkedTokenSource(cancellationToken);

    timeout.CancelAfter(TimeSpan.FromSeconds(25));

    var endpoint = new Uri(httpClient.BaseAddress!, "mcp");
    var query =
        $"Visitor attractions in {destination.Name}, " +
        $"{destination.Country}: names and descriptions.";

    try
    {
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            },
            httpClient);

        await using var client = await McpClient.CreateAsync(
            transport, cancellationToken: timeout.Token);

        var result = await client.CallToolAsync(
            "search_travel_knowledge",
            new Dictionary<string, object?>
            {
                ["destinationId"] = destination.Id,
                ["query"] = query
            },
            cancellationToken: timeout.Token);

        timeout.Token.ThrowIfCancellationRequested();

        if (result.IsError == true)
        {
            logger.LogWarning("Travel knowledge MCP tool failed.");
            return Empty("unavailable");
        }

        if (result.StructuredContent is not
            { ValueKind: JsonValueKind.Object } payload)
        {
            logger.LogWarning("Travel knowledge MCP tool returned no JSON object.");
            return Empty("invalid_response");
        }

        var lookup = payload.Deserialize<TravelKnowledgeLookup>(
            JsonSerializerOptions.Web);

        if (!IsValid(lookup, destination.Id))
        {
            logger.LogWarning("Travel knowledge MCP response failed validation.");
            return Empty("invalid_response");
        }

        logger.LogInformation(
            "Travel knowledge MCP lookup returned {PassageCount} passages",
            lookup!.Passages.Count);

        return lookup;
    }
    catch (OperationCanceledException)
        when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogWarning("Travel knowledge MCP lookup timed out.");
        return Empty("unavailable");
    }
    catch (JsonException)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogWarning("Travel knowledge MCP tool returned invalid JSON.");
        return Empty("invalid_response");
    }
    catch (Exception exception) when (
        exception is McpException or HttpRequestException
            or ClientTransportClosedException)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogWarning(
            "Travel knowledge MCP request failed. Exception type {ExceptionType}",
            exception.GetType().Name);

        return Empty("unavailable");
    }
}
    private static bool IsValid(
        TravelKnowledgeLookup? result,
        int expectedDestinationId)
    {
        if (result is null ||
            result.DestinationId != expectedDestinationId ||
            result.Passages is null ||
            result.Passages.Count > 5)
        {
            return false;
        }

        if (result.Status == "no_evidence")
        {
            return result.Passages.Count == 0;
        }

        if (result.Status != "retrieved" ||
            result.Passages.Count == 0)
        {
            return false;
        }

        if (result.Passages.Any(passage =>
                passage is null ||
                string.IsNullOrWhiteSpace(passage.ChunkId) ||
                passage.DocumentId == Guid.Empty ||
                string.IsNullOrWhiteSpace(passage.DocumentName) ||
                passage.PageNumber < 1 ||
                string.IsNullOrWhiteSpace(passage.Passage) ||
                passage.Passage.Length > 3000))
        {
            return false;
        }

        return result.Passages
            .Select(passage => passage.ChunkId)
            .Distinct(StringComparer.Ordinal)
            .Count() == result.Passages.Count;
    }
}