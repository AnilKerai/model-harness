using System.Diagnostics;
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
/// <see cref="OpenTelemetryTracer"/>'s job. Set <paramref name="enableSensitiveData"/> to also include
/// each model response's <em>text</em> in its event detail, so a UI can show the run's result (the final
/// answer is the last model response's text). Off by default so no model output leaves the process; the
/// dashboard sample turns it on because it is a local operator console.</para>
/// </summary>
/// <param name="enableSensitiveData">Include model response text in <c>model</c> event detail. Default <see langword="false"/>.</param>
public sealed class LiveDashboardTracer(bool enableSensitiveData = false) : ITracer
{
    private const int RingCapacity = 500;

    private readonly object _gate = new();
    private readonly Queue<DashboardEvent> _ring = new(RingCapacity);
    private readonly List<Channel<DashboardEvent>> _subscribers = [];
    private long _seq;

    /// <summary>
    /// Streams events to one subscriber: first the ring-buffer backlog (so a late-joining browser
    /// catches up), then every subsequent event live until <paramref name="ct"/> cancels. A slow
    /// reader never blocks the harness — its channel drops the oldest queued event instead.
    /// </summary>
    public async IAsyncEnumerable<DashboardEvent> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<DashboardEvent>(
            new BoundedChannelOptions(RingCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        DashboardEvent[] backlog;
        lock (_gate)
        {
            backlog = [.. _ring];
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

    // ponytail: single lock guards the ring + subscriber list; a publish is a few non-blocking
    // TryWrites (DropOldest never blocks), so it can't stall the loop thread that called it.
    private void Publish(string kind, string taskId, int? turn, string summary, object? detail = null)
    {
        var evt = new DashboardEvent(Interlocked.Increment(ref _seq), kind, taskId, turn, summary, detail);
        lock (_gate)
        {
            if (_ring.Count == RingCapacity) _ring.Dequeue();
            _ring.Enqueue(evt);
            foreach (var s in _subscribers) s.Writer.TryWrite(evt);
        }
    }

    // Run-level events carry a null turn so the UI renders them outside any per-turn group.
    public void StartTrace(string taskId, string taskText) =>
        Publish("run", taskId, null, Truncate(taskText, 300));

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
        Publish("sensor", taskId, turn, $"{sensorName} @ {hookPoint}: {verdict}",
            new
            {
                sensor = sensorName,
                hookPoint = hookPoint.ToString(),
                verdict,
                reason = result.Reason,
                inputTokens = result.Usage?.InputTokens ?? 0,
                outputTokens = result.Usage?.OutputTokens ?? 0,
                cost = result.Cost ?? 0m,
            });
    }

    public void LogGuideError(string taskId, int turn, string guideName, string error) =>
        Publish("warn", taskId, turn, $"Guide {guideName} degraded: {error}");

    public void LogCompaction(string taskId, int turn, CompactionTrace trace) =>
        Publish("compaction", taskId, turn,
            $"Compacted: reclaimed {trace.TokensReclaimed} tok (evicted {trace.StepsEvicted} steps)",
            new { trace.TokensReclaimed, trace.StepsEvicted, trace.Folded });

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
