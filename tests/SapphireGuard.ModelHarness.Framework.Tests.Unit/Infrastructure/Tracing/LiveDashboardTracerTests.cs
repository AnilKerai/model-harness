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
    public async Task Subscribe_WithAfterSeq_SkipsAlreadySeenEvents_ThenStreamsLiveEvents()
    {
        var tracer = PublishRateLimits(5); // seq 1..5

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token, afterSeq: 3).GetAsyncEnumerator();

        Assert.True(await feed.MoveNextAsync());
        Assert.Equal(4, feed.Current.Seq);
        Assert.True(await feed.MoveNextAsync());
        Assert.Equal(5, feed.Current.Seq);

        tracer.LogRateLimit("t", 0, TimeSpan.FromSeconds(1));
        Assert.True(await feed.MoveNextAsync());
        Assert.Equal(6, feed.Current.Seq);
    }

    [Fact]
    public async Task Subscribe_WithAfterSeqAtTheNewestSeq_ReplaysNothing_ThenStreamsLiveEvents()
    {
        var tracer = PublishRateLimits(3);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = tracer.Subscribe(cts.Token, afterSeq: 3).GetAsyncEnumerator();

        var next = feed.MoveNextAsync();
        tracer.LogRateLimit("t", 0, TimeSpan.FromSeconds(1));

        Assert.True(await next);
        Assert.Equal(4, feed.Current.Seq);
    }

    [Fact]
    public async Task Subscribe_WithAfterSeqZero_ReplaysTheWholeBacklog()
    {
        var tracer = PublishRateLimits(5);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seqs = await TakeSeqsAsync(tracer.Subscribe(cts.Token, afterSeq: 0), 5);

        Assert.Equal([1L, 2, 3, 4, 5], seqs);
    }

    [Fact]
    public async Task Subscribe_WithAfterSeqBeyondTheNewestSeq_ReplaysTheWholeBacklog()
    {
        // The client last saw seq 99 from an earlier process; this one restarted, so seq began again at 1.
        var tracer = PublishRateLimits(3);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seqs = await TakeSeqsAsync(tracer.Subscribe(cts.Token, afterSeq: 99), 3);

        Assert.Equal([1L, 2, 3], seqs);
    }

    [Fact]
    public async Task Publish_FromConcurrentRuns_DeliversEventsInSeqOrder()
    {
        const int total = 8 * 50;
        var tracer = new LiveDashboardTracer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var live = tracer.Subscribe(cts.Token).GetAsyncEnumerator();
        var first = live.MoveNextAsync(); // registers the subscriber before anything is published
        await PublishConcurrentlyAsync(tracer, publishers: 8, perPublisher: 50);

        var liveSeqs = new List<long>();
        var more = await first;
        while (more)
        {
            liveSeqs.Add(live.Current.Seq);
            if (liveSeqs.Count == total) break;
            more = await live.MoveNextAsync();
        }

        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), liveSeqs);
        Assert.Equal(liveSeqs, await TakeSeqsAsync(tracer.Subscribe(cts.Token), total)); // backlog order too
    }

    [Fact]
    public async Task Subscribe_ResumedFromLastSeenSeqWhileRunsPublish_NeitherLosesNorDuplicatesEvents()
    {
        const int total = 8 * 50;
        var tracer = new LiveDashboardTracer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // First connection sees a prefix of the stream, then drops while the runs keep publishing.
        await using var firstConnection = tracer.Subscribe(cts.Token).GetAsyncEnumerator();
        var pending = firstConnection.MoveNextAsync();
        var publishing = PublishConcurrentlyAsync(tracer, publishers: 8, perPublisher: 50);

        var seen = new List<long>();
        var more = await pending;
        while (more)
        {
            seen.Add(firstConnection.Current.Seq);
            if (seen.Count == 100) break;
            more = await firstConnection.MoveNextAsync();
        }
        await firstConnection.DisposeAsync();

        // EventSource reconnects with Last-Event-ID = the last id it received.
        var resumed = await TakeSeqsAsync(tracer.Subscribe(cts.Token, afterSeq: seen[^1]), total - (int)seen[^1]);
        await publishing;

        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), seen.Concat(resumed));
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

    private static LiveDashboardTracer PublishRateLimits(int count)
    {
        var tracer = new LiveDashboardTracer();
        for (var i = 0; i < count; i++) tracer.LogRateLimit("t", 0, TimeSpan.FromSeconds(1));
        return tracer;
    }

    private static async Task<List<long>> TakeSeqsAsync(IAsyncEnumerable<DashboardEvent> feed, int count)
    {
        var seqs = new List<long>();
        await foreach (var e in feed)
        {
            seqs.Add(e.Seq);
            if (seqs.Count == count) break;
        }
        return seqs;
    }

    // Released from a common gate so the publishers genuinely overlap, as concurrent runs and sub-agents do.
    private static Task PublishConcurrentlyAsync(LiveDashboardTracer tracer, int publishers, int perPublisher)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, publishers).Select(p => Task.Run(async () =>
        {
            await gate.Task;
            for (var i = 0; i < perPublisher; i++) tracer.LogRateLimit($"run{p}", i, TimeSpan.FromSeconds(1));
        })).ToArray();
        gate.SetResult();
        return Task.WhenAll(tasks);
    }
}
