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
    /// Called after each model call with the full prompt, tool definitions, and response.
    /// <paramref name="turn"/> is the zero-based turn index the call belongs to, shared by
    /// every event on the same turn so a backend can group them.
    /// </summary>
    void LogModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response);

    /// <summary>Called after each tool execution with the call, result, and wall-clock duration. <paramref name="turn"/> is the zero-based turn index the call belongs to.</summary>
    void LogToolCall(string taskId, int turn, ToolCall call, ToolResult result, TimeSpan duration);

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
