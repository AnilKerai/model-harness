using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tracing;
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
        Assert.Equal("t1", feed.Current.TaskId);

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
        // A leaked pass would arrive first with no verdict; the intervene carries "intervene".
        Assert.Equal("intervene", Detail(feed.Current).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task LogSensorResult_WithSensitiveDataOff_OmitsTheInterventionReason()
    {
        var tracer = new LiveDashboardTracer(); // enableSensitiveData defaults to off
        tracer.LogSensorResult("t", 0, HookPoint.PostModelCall, "pii", SensorResult.Intervene(PiiReason));

        var evt = await FirstEventAsync(tracer);

        Assert.Equal("intervene", Detail(evt).GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, Detail(evt).GetProperty("reason").ValueKind);
        // Nothing the reason quoted may leak through the summary or anywhere else in the detail.
        Assert.DoesNotContain(PiiMatch, evt.Summary);
        Assert.DoesNotContain(PiiMatch, JsonSerializer.Serialize(evt.Detail));
    }

    [Fact]
    public async Task LogSensorResult_WithSensitiveDataOff_StillPublishesAnErrorReason()
    {
        var tracer = new LiveDashboardTracer();
        tracer.LogSensorResult("t", 0, HookPoint.PostModelCall, "critic", SensorResult.Failed("verdict unparseable"));

        var evt = await FirstEventAsync(tracer);

        Assert.Equal("error", Detail(evt).GetProperty("verdict").GetString());
        Assert.Equal("verdict unparseable", Detail(evt).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task LogSensorResult_WithSensitiveDataOn_PublishesTheInterventionReason()
    {
        var tracer = new LiveDashboardTracer(enableSensitiveData: true);
        tracer.LogSensorResult("t", 0, HookPoint.PostModelCall, "pii", SensorResult.Intervene(PiiReason));

        var evt = await FirstEventAsync(tracer);

        Assert.Equal(PiiReason, Detail(evt).GetProperty("reason").GetString());
        Assert.DoesNotContain(PiiMatch, evt.Summary); // the summary never carries the reason
    }

    [Fact]
    public async Task StartTrace_WithSensitiveDataOff_LabelsTheRunWithItsIdNotTheTaskText()
    {
        var tracer = new LiveDashboardTracer();
        tracer.StartTrace("run-42", "my SSN is 123-45-6789");

        var evt = await FirstEventAsync(tracer);

        Assert.Equal("run", evt.Kind);
        Assert.Equal("run-42", evt.Summary);
    }

    [Fact]
    public async Task StartTrace_WithSensitiveDataOn_PublishesTheTaskTextTruncated()
    {
        var tracer = new LiveDashboardTracer(enableSensitiveData: true);
        tracer.StartTrace("run-42", "do the thing");
        tracer.StartTrace("run-43", new string('x', 400));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token).GetAsyncEnumerator();

        Assert.True(await feed.MoveNextAsync());
        Assert.Equal("do the thing", feed.Current.Summary);
        Assert.True(await feed.MoveNextAsync());
        Assert.Equal(new string('x', 300) + "…", feed.Current.Summary);
    }

    [Fact]
    public async Task LogCompaction_WithAnError_ReportsTheFallbackNotASuccess()
    {
        var tracer = new LiveDashboardTracer();
        tracer.LogCompaction("t", 0,
            new CompactionTrace(StepsEvicted: 2, TokensReclaimed: 100, Folded: false, Usage.Zero, Cost: 0m)
            {
                Error = "HttpRequestException: 503"
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token).GetAsyncEnumerator();

        Assert.True(await feed.MoveNextAsync()); // replayed from the backlog
        Assert.Equal("compaction", feed.Current.Kind);
        Assert.Contains("HttpRequestException: 503", feed.Current.Summary);
        Assert.DoesNotContain("Compacted", feed.Current.Summary);
    }

    // A reason as PiiRedactionSensor writes it: it quotes the match it found.
    private const string PiiMatch = "123-45-6789";
    private const string PiiReason =
        $"Response contains possible PII (SSN: \"{PiiMatch}\"). Restate your answer without including any personal data.";

    private static JsonElement Detail(DashboardEvent evt) => JsonSerializer.SerializeToElement(evt.Detail);

    // Reads the first replayed event, so call it after publishing.
    private static async Task<DashboardEvent> FirstEventAsync(LiveDashboardTracer tracer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token).GetAsyncEnumerator();
        Assert.True(await feed.MoveNextAsync());
        return feed.Current;
    }
}
