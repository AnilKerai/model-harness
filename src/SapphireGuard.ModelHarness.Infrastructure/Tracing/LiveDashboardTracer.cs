using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

/// <summary>
/// One live event in the dashboard feed. <paramref name="Detail"/> is an optional bag of
/// structured fields (an anonymous object) the browser cherry-picks — there is no shared schema,
/// so each event kind carries whatever numbers make sense for it.
/// </summary>
public sealed record DashboardEvent(long Seq, string Kind, string TaskId, int? Turn, string Summary, object? Detail = null);

/// <summary>
/// An <see cref="ITracer"/> that feeds a live in-process console: every loop event becomes a
/// <see cref="DashboardEvent"/> pushed to any number of subscribers (e.g. a Server-Sent-Events
/// endpoint) and kept in a bounded ring buffer so a browser that connects mid-run still sees the
/// history. Zero external dependencies — no OTLP, no collector, no Docker. Compose it alongside
/// <see cref="OpenTelemetryTracer"/> via the builder (both run through <see cref="CompositeTracer"/>):
/// the dashboard is the local operator console, OTLP is the durable backend.
/// <para>Surfaces <em>metadata and short summaries</em> by default — token counts, cost, tool names,
/// sensor verdicts, budget burn-down — never full prompt bodies or tool-result content, which remain
/// <see cref="OpenTelemetryTracer"/>'s job. Conversation content is gated behind
/// <paramref name="enableSensitiveData"/>, matching <see cref="OpenTelemetryTracer"/>. With it off (the
/// default) no conversation content leaves the process:</para>
/// <list type="bullet">
/// <item>the <c>run</c> event's summary is the task id, not the task text;</item>
/// <item>a <c>sensor</c> intervention carries no <c>reason</c> — a reason can quote the content the sensor
/// flagged (a PII match, a tool-error body, model text);</item>
/// <item>a <c>model</c> event carries no response <c>text</c>.</item>
/// </list>
/// <para>With it on, the <c>run</c> summary is the first 300 characters of the task text, an intervention's
/// <c>reason</c> is published, and each <c>model</c> event includes the response <em>text</em> (the final
/// answer is the last model response's text, which a UI can show as the run's result). A sensor
/// <em>error</em>'s reason is published either way: it describes the failure, not flagged content, so a
/// failing sensor stays diagnosable without content capture (ADR-001). Failure messages (guide, compaction,
/// model call, run) are likewise always published, as they are in <see cref="OpenTelemetryTracer"/>.
/// Off by default; the dashboard sample turns it on because it is a local operator console.</para>
/// </summary>
/// <param name="enableSensitiveData">Include the task text, intervention reasons and model response text in
/// event summaries and detail. Default <see langword="false"/>.</param>
public sealed class LiveDashboardTracer(bool enableSensitiveData = false) : ITracer
{
    private const int RingCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<DashboardEvent> _ring = new(RingCapacity);
    private readonly List<Channel<DashboardEvent>> _subscribers = [];
    private long _seq;

    /// <summary>
    /// Identifies this tracer instance (a new GUID per instance, so per process start, and distinct across
    /// load-balanced instances). <see cref="DashboardEvent.Seq"/> only means something within one epoch: it
    /// restarts at 1 with every new tracer, so a resume token must carry the epoch it was issued under —
    /// see <see cref="EventId"/>.
    /// </summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("n");

    /// <summary>
    /// The resume token for <paramref name="e"/>, <c>{Epoch}-{Seq}</c>: write it as the SSE <c>id:</c> field
    /// and hand the <c>Last-Event-ID</c> a reconnecting client sends back to
    /// <see cref="Subscribe(string?, CancellationToken)"/>.
    /// </summary>
    public string EventId(DashboardEvent e) => $"{Epoch}-{e.Seq}";

    /// <summary>
    /// Streams every event to one subscriber: first the ring-buffer backlog (so a late-joining browser
    /// catches up), then every subsequent event live until <paramref name="ct"/> cancels. A slow
    /// reader never blocks the harness — its channel drops the oldest queued event instead.
    /// </summary>
    public IAsyncEnumerable<DashboardEvent> Subscribe(CancellationToken ct) => Subscribe(0, ct);

    /// <summary>
    /// Like <see cref="Subscribe(CancellationToken)"/>, but resumes a dropped connection from the resume
    /// token (<see cref="EventId"/>) the client last received, i.e. the SSE <c>Last-Event-ID</c> header.
    /// Backlog events up to and including that one are skipped so the client does not see them twice. The
    /// resume only applies when the token's epoch is this tracer's <see cref="Epoch"/>; a token from another
    /// epoch (the process restarted, or a different instance served the earlier connection), a missing or
    /// empty one, or one that cannot be parsed, replays the whole backlog. Live events are never filtered.
    /// </summary>
    /// <param name="lastEventId">The raw <c>Last-Event-ID</c> value, or <see langword="null"/> if the client sent none.</param>
    /// <param name="ct">Cancels the stream.</param>
    public IAsyncEnumerable<DashboardEvent> Subscribe(string? lastEventId, CancellationToken ct) =>
        Subscribe(ResumeSeq(lastEventId), ct);

    /// <summary>
    /// Like <see cref="Subscribe(CancellationToken)"/>, but resumes after a seq: backlog events with
    /// <see cref="DashboardEvent.Seq"/> at or below <paramref name="afterSeq"/> are skipped, and live events
    /// are never filtered. A bare seq carries no epoch, so this overload <b>cannot detect a restart</b>: seq
    /// began again at 1, and the process may already have published as many events as the old one, so a
    /// stale <paramref name="afterSeq"/> silently skips new events. As a heuristic only, an
    /// <paramref name="afterSeq"/> beyond the newest seq is taken to mean the process restarted and the whole
    /// backlog is replayed. Prefer <see cref="Subscribe(string?, CancellationToken)"/> for a reconnecting
    /// client; use this when the caller holds the seq from the same tracer instance (in-process).
    /// </summary>
    /// <param name="afterSeq">Seq of the last event the client already has; <c>0</c> replays the whole backlog.</param>
    /// <param name="ct">Cancels the stream.</param>
    public async IAsyncEnumerable<DashboardEvent> Subscribe(
        long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<DashboardEvent>(
            new BoundedChannelOptions(RingCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        DashboardEvent[] backlog;
        lock (_gate)
        {
            var skipThrough = afterSeq > _seq ? 0 : afterSeq;
            backlog = [.. _ring.Where(e => e.Seq > skipThrough)];
            _subscribers.Add(channel);
        }

        try
        {
            foreach (var e in backlog) yield return e;
            await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    // A resume token is "{epoch}-{seq}" (EventId). Only a token issued by this instance (epoch matches, seq a
    // plain non-negative number) resumes; anything else — another epoch, no/empty id, garbage, a legacy
    // seq-only id — is 0, i.e. replay the whole backlog. The epoch is a GUID in "n" format, so it has no '-'.
    private long ResumeSeq(string? lastEventId)
    {
        var dash = lastEventId?.IndexOf('-') ?? -1;
        return dash > 0
            && lastEventId.AsSpan(0, dash).Equals(Epoch, StringComparison.Ordinal)
            && long.TryParse(lastEventId.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                ? seq
                : 0;
    }

    // ponytail: single lock guards the ring + subscriber list; a publish is a few non-blocking
    // TryWrites (DropOldest never blocks), so it can't stall the loop thread that called it.
    // Seq is assigned inside the lock so ring/delivery order is seq order — Subscribe's afterSeq
    // resume (and its "afterSeq beyond newest" heuristic) relies on that.
    private void Publish(string kind, string taskId, int? turn, string summary, object? detail = null)
    {
        lock (_gate)
        {
            var evt = new DashboardEvent(++_seq, kind, taskId, turn, summary, detail);
            if (_ring.Count == RingCapacity) _ring.Dequeue();
            _ring.Enqueue(evt);
            foreach (var s in _subscribers) s.Writer.TryWrite(evt);
        }
    }

    // Run-level events carry a null turn so the UI renders them outside any per-turn group. The task text
    // is conversation content, so without enableSensitiveData the run is labelled by its id alone.
    public void StartTrace(string taskId, string taskText) =>
        Publish("run", taskId, null, enableSensitiveData ? Truncate(taskText, 300) : taskId);

    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools)
    {
        Publish("model:start", taskId, turn, $"Turn {turn + 1}: calling model ({prompt.Count} msgs, {tools.Count} tools)");
        return new ModelCallScope(this, taskId, turn, enableSensitiveData);
    }

    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) =>
        new ToolCallScope(this, taskId, turn, call.ToolName);

    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        // Passes would drown the feed — only interventions and errors are worth an operator's eye.
        if (!result.IsIntervene && !result.IsError) return;
        var verdict = result.IsError ? "error" : "intervene";
        // An intervention's reason can quote the content the sensor flagged (PII, a tool-error body, model
        // text), so it is gated; an error's reason describes the failure and stays visible (ADR-001).
        // Keep the reason out of the summary string too, which is never gated.
        var reason = result.IsError || enableSensitiveData ? result.Reason : null;
        Publish("sensor", taskId, turn, $"{sensorName} @ {hookPoint}: {verdict}",
            new
            {
                sensor = sensorName,
                hookPoint = hookPoint.ToString(),
                verdict,
                reason,
                inputTokens = result.Usage?.InputTokens ?? 0,
                outputTokens = result.Usage?.OutputTokens ?? 0,
                cost = result.Cost ?? 0m,
            });
    }

    public void LogGuideError(string taskId, int turn, string guideName, string error) =>
        Publish("warn", taskId, turn, $"Guide {guideName} degraded: {error}");

    public void LogCompaction(string taskId, int turn, CompactionTrace trace) =>
        Publish("compaction", taskId, turn,
            trace.Error is { } error
                ? $"Compaction fell back ({trace.StepsEvicted} steps omitted): {error}"
                : $"Compacted: reclaimed {trace.TokensReclaimed} tok (evicted {trace.StepsEvicted} steps)",
            new { trace.TokensReclaimed, trace.StepsEvicted, trace.Folded, trace.Error });

    public void LogRateLimit(string taskId, int turn, TimeSpan delay) =>
        Publish("ratelimit", taskId, turn, $"Rate-limit backoff: {delay.TotalSeconds:0.#}s",
            new { seconds = delay.TotalSeconds });

    public void LogCheckpoint(string taskId, int turn, string checkpointId, TimeSpan elapsed) =>
        Publish("checkpoint", taskId, turn, $"Checkpoint saved ({elapsed.TotalMilliseconds:0} ms)",
            new { checkpointId, ms = elapsed.TotalMilliseconds });

    public void LogBudgetSnapshot(string taskId, int turn, BudgetSnapshot snapshot) =>
        Publish("budget", taskId, turn,
            $"turns {snapshot.TurnsUsed}/{snapshot.MaxTurns}, {snapshot.TokensUsed} tok, ${snapshot.CostUsed:0.####}",
            new
            {
                snapshot.TurnsUsed,
                snapshot.MaxTurns,
                snapshot.TokensUsed,
                snapshot.MaxTotalTokens,
                costUsed = snapshot.CostUsed,
                costMax = snapshot.MaxCost,
                wallclockSeconds = snapshot.Elapsed.TotalSeconds,
                wallclockMaxSeconds = snapshot.MaxWallClock.TotalSeconds,
            });

    public void Complete(string taskId, AgentStatus status, string? failureReason) =>
        Publish("done", taskId, null, failureReason is null ? $"Finished: {status}" : $"Finished: {status} — {failureReason}",
            new { status = status.ToString(), failureReason });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class ModelCallScope(LiveDashboardTracer owner, string taskId, int turn, bool enableSensitiveData) : IModelCallScope
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _completed;

        public void Complete(ModelResponse response)
        {
            _completed = true;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            owner.Publish("model", taskId, turn,
                $"{response.Model ?? "model"}: {response.Usage.InputTokens}→{response.Usage.OutputTokens} tok, ${response.Cost:0.####} ({ms:0} ms)",
                new
                {
                    model = response.Model,
                    provider = response.Provider,
                    response.Usage.InputTokens,
                    response.Usage.OutputTokens,
                    cachedTokens = response.CachedInputTokens,
                    cost = response.Cost,
                    finish = response.StopReason.ToString(),
                    toolCalls = response.ToolCalls.Count,
                    ms,
                    // Off by default (see enableSensitiveData); the final answer is the last model text.
                    text = enableSensitiveData ? response.Text : null,
                });
        }

        public void Fail(Exception exception)
        {
            _completed = true;
            owner.Publish("error", taskId, turn, $"Model call failed: {exception.Message}",
                new { type = exception.GetType().Name });
        }

        public void Dispose()
        {
            if (!_completed)
                owner.Publish("error", taskId, turn, "Model call did not complete");
        }
    }

    private sealed class ToolCallScope(LiveDashboardTracer owner, string taskId, int turn, string toolName) : IToolCallScope
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _completed;

        public void Complete(ToolResult result)
        {
            _completed = true;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            owner.Publish("tool", taskId, turn,
                $"{toolName} {(result.IsError ? "✗" : "✓")} ({ms:0} ms)",
                new { name = toolName, isError = result.IsError, ms });
        }

        public void Dispose()
        {
            if (!_completed)
                owner.Publish("error", taskId, turn, $"Tool {toolName} did not complete");
        }
    }
}
