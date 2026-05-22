using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

public sealed class NullTracer : ITracer
{
    public void StartTrace(string taskId, string taskText) { }
    public void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response) { }
    public void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration) { }
    public void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result) { }
    public void Complete(string taskId, AgentStatus status, string? failureReason) { }
}
