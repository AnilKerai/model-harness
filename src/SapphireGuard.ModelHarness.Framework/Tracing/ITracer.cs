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

    /// <summary>Called after each sensor evaluation that produces an intervention. <paramref name="turn"/> is the zero-based turn index the evaluation belongs to.</summary>
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
}

/// <summary>Scope bracketing a single model call. Complete it with the response, then dispose (a plain <c>using</c> does this). Disposal without <see cref="Complete"/> marks the span failed.</summary>
public interface IModelCallScope : IDisposable
{
    void Complete(ModelResponse response);
}

/// <summary>Scope bracketing a single tool execution. Complete it with the result, then dispose.</summary>
public interface IToolCallScope : IDisposable
{
    void Complete(ToolResult result);
}
