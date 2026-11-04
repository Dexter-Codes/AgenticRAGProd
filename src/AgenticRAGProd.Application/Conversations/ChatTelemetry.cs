using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

namespace AgenticRAGProd.Application.Conversations;
public sealed class ChatTelemetry(ILogger<ChatTelemetry> logger)
{
    public const string ActivitySourceName = "AgenticRAGProd.Application.Chat";
    private static readonly ActivitySource Activities = new(ActivitySourceName);
    public const string MeterName = "AgenticRAGProd.Application.Chat";
    public const string DurationMetricName = "agenticrag.chat.operation.duration";
    private static readonly Meter Metrics = new(MeterName);

    private static readonly Counter<long> AnswerOutcomes =
    Metrics.CreateCounter<long>(
        name: "agenticrag.chat.answer.outcomes",
        unit: "{answer}",
        description:
            "Answer outcomes after confirmed exchange persistence.");
    private static readonly Counter<long> CompletedOperations =
        Metrics.CreateCounter<long>(
            name: "agenticrag.chat.operations",
            unit: "{operation}",
            description: "Number of completed chat stage operations.");

    private static readonly Histogram<double> OperationDuration =
        Metrics.CreateHistogram<double>(
            name: DurationMetricName,
            unit: "s",
            description: "Duration of chat stage operations.");

    public IDisposable? BeginConversation(Guid conversationId, string messageId)
    {
        return logger.BeginScope(new Dictionary<string, object>
        {
            ["ConversationId"] = conversationId,
            ["MessageId"] = messageId
        });
    }

    public Task MeasureAsync(
        string stage,
        Func<Task> operation)
    {
        return MeasureAsync(stage, async () =>
        {
            await operation();
            return true;
        });
    }

    public async Task<T> MeasureAsync<T>(
    string stage,
     Func<Activity?, Task<T>> operation)
    {
        using var activity = Activities.StartActivity(
            stage,
            ActivityKind.Internal);

        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        string? exceptionType = null;

        try
        {
            return await operation(activity);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";

            // Caller cancellation and internal timeout need
            // separate classification at the request boundary.
            throw;
        }
        catch (Exception exception)
        {
            outcome = "failed";
            exceptionType = exception.GetType().FullName;

            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exceptionType);

            throw;
        }
        finally
        {
            activity?.SetTag("chat.outcome", outcome);

            var elapsed = Stopwatch.GetElapsedTime(started);

            var tags = new TagList
            {
                { "stage", stage },
                { "outcome", outcome }
            };

            CompletedOperations.Add(1, tags);
            OperationDuration.Record(elapsed.TotalSeconds, tags);

            var elapsedMs = elapsed.TotalMilliseconds;

            var level = outcome == "failed"
                ? LogLevel.Warning
                : LogLevel.Information;

            logger.Log(
                level,
                "Chat stage {Stage} completed with {Outcome} " +
                "in {ElapsedMs} ms. Trace ID: {TraceId}; " +
                "Span ID: {SpanId}; Exception type: {ExceptionType}",
                stage,
                outcome,
                elapsedMs,
                Activity.Current?.TraceId.ToString(),
                Activity.Current?.SpanId.ToString(),
                exceptionType);
        }
        
    }
   
    public Task<T> MeasureAsync<T>(
    string stage,
    Func<Task<T>> operation)
    {
        return MeasureAsync(stage, _ => operation());
    }

    public static string AnswerKindTag(ChatAnswerKind kind) =>
    kind switch
    {
        ChatAnswerKind.Answered => "answered",
        ChatAnswerKind.Clarification => "clarification",
        ChatAnswerKind.InsufficientEvidence =>
            "insufficient_evidence",
        _ => "unknown"
    };

    public void RecordAnswerOutcome(
        Activity? activity,
        ChatAnswer answer)
    {
        var kind = AnswerKindTag(answer.Kind);

        activity?.SetTag("rag.answer.kind", kind);
        activity?.SetTag(
            "rag.evidence.source_count",
            answer.Sources.Count);

        AnswerOutcomes.Add(
            1,
            new KeyValuePair<string, object?>(
                "rag.answer.kind",
                kind));

        logger.LogInformation(
            "Chat answer kind {AnswerKind}; source count " +
            "{SourceCount}. Trace ID: {TraceId}",
            kind,
            answer.Sources.Count,
            Activity.Current?.TraceId.ToString());
    }
}