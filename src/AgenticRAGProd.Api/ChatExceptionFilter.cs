using System.Diagnostics;
using System.ClientModel;
using Azure;
using AgenticRAGProd.Application.Conversations;

namespace AgenticRAGProd.Api;

internal sealed class ChatExceptionFilter(ILogger<ChatExceptionFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (ChatInputException error)
        {
            return Results.Problem(statusCode: 400, title: "Invalid request", detail: error.Message);
        }
        catch (ConversationNotFoundException)
        {
            return Results.Problem(statusCode: 404, title: "Conversation not found");
        }
        catch (ChatStorageException error)
        {
            LogFailure(context.HttpContext, error);
            return Results.Problem(statusCode: 503, title: "Chat storage configuration error", detail: error.Message);
        }
        catch (ChatGenerationException error)
        {
            return Results.Problem(statusCode: 502, title: "Generation failed", detail: error.Message);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Results.Problem(statusCode: 504, title: "Chat operation timed out",
                detail: "The operation did not complete in time. Inspect saved chat messages before submitting again.");
        }
        catch (Exception error)
        {
            // Keep provider details and document contents out of responses and logs.
            LogFailure(context.HttpContext, error);
            return Results.Problem(statusCode: 503, title: "Chat operation failed",
                detail: "The operation could not be confirmed. Inspect saved chat messages before submitting again.");
        }
    }

    private void LogFailure(HttpContext context, Exception error)
    {
        var cause = error.GetBaseException();
        int? providerStatus = cause switch
        {
            ClientResultException model => model.Status,
            RequestFailedException azure => azure.Status,
            HttpRequestException http when http.StatusCode.HasValue => (int)http.StatusCode.Value,
            _ => null
        };
        logger.LogWarning("Chat operation failed. Trace ID: {TraceId}; Request ID: {RequestId}; Exception type: {ExceptionType}; Provider status: {ProviderStatus}",
            Activity.Current?.Id ?? context.TraceIdentifier, context.TraceIdentifier, cause.GetType().FullName, providerStatus);
    }
}
