using System.Collections.Concurrent;
using System.Diagnostics;
using AgenticRAGProd.Application.Conversations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgenticRAGProd.Tests.Chat;

public sealed class ChatTracingTests
{
    [Fact]
    public async Task NestedOperationsShareTraceAndLinkToParent()
    {
        var stopped = new ConcurrentBag<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == ChatTelemetry.ActivitySourceName,

            Sample =
                (ref ActivityCreationOptions<ActivityContext> options) =>
                    ActivitySamplingResult.AllDataAndRecorded,

            ActivityStopped = stopped.Add
        };

        ActivitySource.AddActivityListener(listener);

        var telemetry = new ChatTelemetry(
            NullLogger<ChatTelemetry>.Instance);

        var result = await telemetry.MeasureAsync(
            "test.parent",
            () => telemetry.MeasureAsync(
                "test.child",
                () => Task.FromResult(42)));

        var parent = Assert.Single(
            stopped,
            activity => activity.OperationName == "test.parent");

        var child = Assert.Single(
            stopped,
            activity => activity.OperationName == "test.child");

        Assert.Equal(42, result);
        Assert.Equal(parent.TraceId, child.TraceId);
        Assert.Equal(parent.SpanId, child.ParentSpanId);
        Assert.Equal("success", child.GetTagItem("chat.outcome"));
    }
}