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

    /// <summary>Called after each model call with the full prompt, tool definitions, and response.</summary>
    void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response);

    /// <summary>Called after each tool execution with the call, result, and wall-clock duration.</summary>
    void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration);

    /// <summary>Called after each sensor evaluation that produces an intervention.</summary>
    void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result);

    /// <summary>Called once when the run finishes with the terminal status and, on failure, a reason.</summary>
    void Complete(string taskId, AgentStatus status, string? failureReason);
}
