using ModelHarness.Framework.Sensors;
using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Tracing;

/// <summary>Sink for structured trace events produced by the loop.</summary>
public interface ITracer
{
    void StartTrace(string taskId, string taskText);

    void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response);

    void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration);

    void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result);

    void Complete(string taskId, AgentStatus status, string? failureReason);
}
