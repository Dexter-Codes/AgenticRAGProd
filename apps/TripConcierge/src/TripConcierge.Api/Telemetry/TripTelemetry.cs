using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;

namespace TripConcierge.Api.Telemetry;

public static class TripTelemetry
{
    public const string SourceName = "TripConcierge";
    public const string DurationMetricName = "trip.stage.duration";

    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Executions =
        Meter.CreateCounter<long>("trip.stage.executions");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(DurationMetricName, "s");

    private static readonly Counter<long> CheckpointReuses =
        Meter.CreateCounter<long>("trip.checkpoint.reuses");

    private static readonly Counter<long> RunReplays =
        Meter.CreateCounter<long>("trip.run.replays");

    private static readonly Counter<long> ModelTokens =
        Meter.CreateCounter<long>("trip.model.tokens", "{token}");

    public static async Task<T> MeasureAsync<T>(
        string stage,
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        Func<T, string>? resultStatus = null)
    {
        using var activity = Source.StartActivity($"trip.{stage}");
        activity?.SetTag("trip.stage", stage);

        var started = Stopwatch.GetTimestamp();
        var outcome = "success";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await operation();

            if (resultStatus is not null)
            {
                activity?.SetTag("trip.result.status", resultStatus(result));
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            activity?.SetTag("trip.cancelled", true);
            throw;
        }
        catch (Exception exception)
        {
            outcome = "error";
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", exception.GetType().Name);
            throw;
        }
        finally
        {
            var tags = new TagList
            {
                { "stage", stage },
                { "outcome", outcome }
            };

            Executions.Add(1, tags);
            Duration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                tags);
        }
    }

    public static void RecordCheckpointReuse(string stage)
    {
        CheckpointReuses.Add(
            1,
            new KeyValuePair<string, object?>("stage", stage));

        Activity.Current?.AddEvent(new ActivityEvent(
            "checkpoint.reused",
            tags: new ActivityTagsCollection
            {
                { "trip.stage", stage }
            }));
    }

    public static void RecordRunReplay()
    {
        RunReplays.Add(1);
        Activity.Current?.SetTag("trip.run.replayed", true);
    }

    public static void RecordModelUsage(string agent, UsageDetails? usage)
    {
        Activity.Current?.SetTag("trip.agent", agent);

        if (usage?.InputTokenCount is { } inputTokens)
        {
            var tags = new TagList
            {
                { "agent", agent },
                { "direction", "input" }
            };

            ModelTokens.Add(inputTokens, tags);
            Activity.Current?.SetTag(
                "gen_ai.usage.input_tokens", inputTokens);
        }

        if (usage?.OutputTokenCount is { } outputTokens)
        {
            var tags = new TagList
            {
                { "agent", agent },
                { "direction", "output" }
            };

            ModelTokens.Add(outputTokens, tags);
            Activity.Current?.SetTag(
                "gen_ai.usage.output_tokens", outputTokens);
        }
    }
}