using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tracing;

public sealed class LiveDashboardTracerTests
{
    [Fact]
    public async Task Subscribe_ReplaysBacklog_ThenStreamsLiveEvents()
    {
        var tracer = new LiveDashboardTracer();
        tracer.StartTrace("t1", "do the thing"); // emitted before anyone subscribes

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token).GetAsyncEnumerator();

        // A browser that connects mid-run still gets the history.
        Assert.True(await feed.MoveNextAsync());
        Assert.Equal("run", feed.Current.Kind);
        Assert.Equal("do the thing", feed.Current.Summary);

        // …and then live events as they happen.
        tracer.LogRateLimit("t1", 0, TimeSpan.FromSeconds(1));
        Assert.True(await feed.MoveNextAsync());
        Assert.Equal("ratelimit", feed.Current.Kind);
    }

    [Fact]
    public async Task LogSensorResult_SkipsPasses_ButEmitsInterventions()
    {
        var tracer = new LiveDashboardTracer();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token).GetAsyncEnumerator();

        var next = feed.MoveNextAsync(); // registers the subscriber, then awaits

        tracer.LogSensorResult("t", 0, HookPoint.PreToolCall, "guard", SensorResult.Pass);
        tracer.LogSensorResult("t", 0, HookPoint.PreToolCall, "guard", SensorResult.Intervene("blocked"));

        Assert.True(await next);
        Assert.Equal("sensor", feed.Current.Kind);
        // A leaked pass would arrive first with a null reason; the intervene carries "blocked".
        Assert.Contains("blocked", JsonSerializer.Serialize(feed.Current.Detail));
    }
}
