using TripConcierge.Api.Travel;

namespace TripConcierge.Api.Workflows;

public static class DevelopmentUiEndpoints
{
    public sealed record KnowledgeRequest(int DestinationId, string? Query);

    public static void MapDevelopmentUiEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapGet("/api/trip-runs/{runId:guid}", (Guid runId, TripRunStore store) =>
        {
            var saved = store.Find(runId);
            return saved is null ? Results.NotFound(new { error = "Trip run not found." })
                : Results.Ok(new { runId, saved.Request, saved.SelectedDestinationId, saved.Draft,
                    saved.Version, checkpoint = store.LoadCheckpoint(runId) });
        });
        app.MapGet("/api/local/capabilities", (HttpContext context, TravelKnowledgeClient client, CancellationToken token) =>
            Forward(context, client, HttpMethod.Get, "api/local/capabilities", token));
        app.MapPost("/api/documents", (HttpContext context, TravelKnowledgeClient client, CancellationToken token) =>
            Forward(context, client, HttpMethod.Post, "api/documents", token));
        app.MapPost("/api/documents/{documentId:guid}/index", (Guid documentId, string documentName,
            HttpContext context, TravelKnowledgeClient client, CancellationToken token) =>
            Forward(context, client, HttpMethod.Post,
                $"api/documents/{documentId:D}/index?documentName={Uri.EscapeDataString(documentName)}", token));
        app.MapPost("/api/chat", (HttpContext context, TravelKnowledgeClient client, CancellationToken token) =>
            Forward(context, client, HttpMethod.Post, "api/chat", token));
        app.MapPost("/api/travel-knowledge/search", (HttpContext context, TravelKnowledgeClient client, CancellationToken token) =>
            Forward(context, client, HttpMethod.Post, "api/travel-knowledge/search", token));
        app.MapGet("/api/mcp/tools", (TravelKnowledgeClient client, CancellationToken token) => Mcp(client, null, token));
        app.MapPost("/api/mcp/search", (KnowledgeRequest request, TravelKnowledgeClient client, CancellationToken token) =>
            Mcp(client, request, token));
    }

    private static async Task<IResult> Mcp(TravelKnowledgeClient client, KnowledgeRequest? request, CancellationToken token)
    {
        if (request is not null && (request.DestinationId <= 0 || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 500))
            return Results.BadRequest(new { error = "Provide a positive destination ID and a query of 1–500 characters." });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            return Results.Ok(await client.InspectMcpAsync(request?.DestinationId, request?.Query, timeout.Token));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return Results.Problem(statusCode: 504, title: "MCP request timed out"); }
        catch (Exception) when (!token.IsCancellationRequested)
        { return Results.Problem(statusCode: 502, title: "MCP server unavailable or returned an invalid response"); }
    }

    private static async Task Forward(HttpContext context, TravelKnowledgeClient client, HttpMethod method, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } type)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        try
        {
            using var response = await client.ForwardAsync(request, token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(context.Response.Body, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !context.Response.HasStarted)
        { await Results.Problem(statusCode: 504, title: "RAG request timed out").ExecuteAsync(context); }
        catch (HttpRequestException) when (!context.Response.HasStarted)
        { await Results.Problem(statusCode: 502, title: "Configured RAG API is unavailable").ExecuteAsync(context); }
    }
}
