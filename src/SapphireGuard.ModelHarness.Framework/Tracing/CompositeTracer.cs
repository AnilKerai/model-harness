using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tracing;

[ExcludeFromCodeCoverage]
public sealed class CompositeTracer(params ITracer[] tracers) : ITracer
{
    public void StartTrace(string taskId, string taskText)
    {
        foreach (var t in tracers) t.StartTrace(taskId, taskText);
    }

    public void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response)
    {
        foreach (var t in tracers) t.LogModelCall(taskId, prompt, tools, response);
    }

    public void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration)
    {
        foreach (var t in tracers) t.LogToolCall(taskId, call, result, duration);
    }

    public void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        foreach (var t in tracers) t.LogSensorResult(taskId, hookPoint, sensorName, result);
    }

    public void Complete(string taskId, AgentStatus status, string? failureReason)
    {
        foreach (var t in tracers) t.Complete(taskId, status, failureReason);
    }
}
