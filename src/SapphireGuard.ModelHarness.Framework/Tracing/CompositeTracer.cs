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

    public void LogModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response)
    {
        foreach (var t in tracers) t.LogModelCall(taskId, turn, prompt, tools, response);
    }

    public void LogToolCall(string taskId, int turn, ToolCall call, ToolResult result, TimeSpan duration)
    {
        foreach (var t in tracers) t.LogToolCall(taskId, turn, call, result, duration);
    }

    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        foreach (var t in tracers) t.LogSensorResult(taskId, turn, hookPoint, sensorName, result);
    }

    public void Complete(string taskId, AgentStatus status, string? failureReason)
    {
        foreach (var t in tracers) t.Complete(taskId, status, failureReason);
    }

    public void LogGuideContribution(string taskId, int turn, string guideName, GuideContribution contribution)
    {
        foreach (var t in tracers) t.LogGuideContribution(taskId, turn, guideName, contribution);
    }
}
