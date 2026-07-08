using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tracing;

/// <summary>
/// Sink for structured trace events emitted by the loop. Register multiple tracers
/// via the builder — they are automatically composed into a <see cref="CompositeTracer"/>.
/// </summary>
public interface ITracer
{
    /// <summary>Called once when a new task begins.</summary>
    void StartTrace(string taskId, string taskText);

    /// <summary>
    /// Opens a scope around a model call, letting a tracer bracket it as a real span with
    /// accurate duration. The loop calls <see cref="IModelCallScope.Complete"/> with the
    /// response, then disposes the scope; a scope disposed without <c>Complete</c> (the call
    /// threw) is recorded as a failure. <paramref name="turn"/> is the zero-based turn index,
    /// shared by every event on the same turn so a backend can group them.
    /// </summary>
    IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools);

    /// <summary>Opens a scope around a tool execution so a tracer can bracket it as a real span. The loop calls <see cref="IToolCallScope.Complete"/> with the result, then disposes the scope. <paramref name="turn"/> is the zero-based turn index the call belongs to.</summary>
    IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call);

    /// <summary>Called after <em>every</em> sensor evaluation — pass, intervention, or error, not only interventions. <paramref name="turn"/> is the zero-based turn index the evaluation belongs to.</summary>
    void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result);

    /// <summary>Called once when the run finishes with the terminal status and, on failure, a reason.</summary>
    void Complete(string taskId, AgentStatus status, string? failureReason);

    /// <summary>
    /// Called after each guide contributes to the context draft, with the structural delta
    /// it produced. <paramref name="turn"/> is the zero-based turn index the contribution
    /// belongs to. Default no-op so existing <see cref="ITracer"/> implementations compile
    /// unchanged — override it to observe how the guide pipeline shapes each turn's context.
    /// </summary>
    void LogGuideContribution(string taskId, int turn, string guideName, GuideContribution contribution) { }

    /// <summary>
    /// Called after the trajectory guide evicts and compacts on a turn. <paramref name="turn"/> is the
    /// zero-based turn index. Default no-op so existing <see cref="ITracer"/> implementations compile
    /// unchanged — override it to observe when compaction fires, how much it reclaimed, and its spend.
    /// </summary>
    void LogCompaction(string taskId, int turn, CompactionTrace trace) { }

    /// <summary>
    /// Called when a supporting guide throws and is skipped (fail-open) so one guide's failure can't
    /// take down the run. <paramref name="error"/> is the exception type and message. <paramref name="turn"/>
    /// is the zero-based turn index. Default no-op so existing <see cref="ITracer"/> implementations compile
    /// unchanged — override it to alert on guides that are silently degrading the context (e.g. a memory
    /// store timing out or a skill store failing to read).
    /// </summary>
    void LogGuideError(string taskId, int turn, string guideName, string error) { }

    /// <summary>
    /// Called when the loop waits on a rate-limit backoff before a turn. <paramref name="delay"/> is
    /// how long it will wait. Default no-op — override to observe throttling (how often, how long).
    /// </summary>
    void LogRateLimit(string taskId, int turn, TimeSpan delay) { }

    /// <summary>
    /// Called after each turn's checkpoint is saved. <paramref name="elapsed"/> is the save duration.
    /// Default no-op — override to observe persistence latency (a slow or failing store is otherwise invisible).
    /// </summary>
    void LogCheckpoint(string taskId, int turn, string checkpointId, TimeSpan elapsed) { }

    /// <summary>
    /// Called once per turn with the run's cumulative resource usage against its budget, so consumption
    /// is observable before exhaustion (not only at the terminal <c>PartialResult</c>). Default no-op —
    /// override to chart budget burn-down per turn.
    /// </summary>
    void LogBudgetSnapshot(string taskId, int turn, BudgetSnapshot snapshot) { }
}

/// <summary>Scope bracketing a single model call. Complete it with the response, then dispose (a plain <c>using</c> does this). Disposal without <see cref="Complete"/> marks the span failed.</summary>
public interface IModelCallScope : IDisposable
{
    void Complete(ModelResponse response);

    /// <summary>
    /// Records that the model call threw, capturing the exception type/message/stack on the span
    /// (a bare disposal-without-<see cref="Complete"/> only marks it failed with no detail). Default
    /// no-op so existing implementations compile unchanged. Cancellation is deliberately not reported
    /// through this — it is expected control flow, not a call failure.
    /// </summary>
    void Fail(Exception exception) { }
}

/// <summary>Scope bracketing a single tool execution. Complete it with the result, then dispose.</summary>
public interface IToolCallScope : IDisposable
{
    void Complete(ToolResult result);
}
