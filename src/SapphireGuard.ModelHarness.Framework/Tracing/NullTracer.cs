using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tracing;

[ExcludeFromCodeCoverage]
public sealed class NullTracer : ITracer
{
    public void StartTrace(string taskId, string taskText) { }
    public void LogModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response) { }
    public void LogToolCall(string taskId, int turn, ToolCall call, ToolResult result, TimeSpan duration) { }
    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) { }
    public void Complete(string taskId, AgentStatus status, string? failureReason) { }
}
