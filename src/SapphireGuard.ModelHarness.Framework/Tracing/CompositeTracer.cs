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

    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools)
        => new CompositeModelCallScope([.. tracers.Select(t => t.BeginModelCall(taskId, turn, prompt, tools))]);

    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call)
        => new CompositeToolCallScope([.. tracers.Select(t => t.BeginToolCall(taskId, turn, call))]);

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

    public void LogCompaction(string taskId, int turn, CompactionTrace trace)
    {
        foreach (var t in tracers) t.LogCompaction(taskId, turn, trace);
    }

    public void LogGuideError(string taskId, int turn, string guideName, string error)
    {
        foreach (var t in tracers) t.LogGuideError(taskId, turn, guideName, error);
    }

    public void LogRateLimit(string taskId, int turn, TimeSpan delay)
    {
        foreach (var t in tracers) t.LogRateLimit(taskId, turn, delay);
    }

    public void LogCheckpoint(string taskId, int turn, string checkpointId, TimeSpan elapsed)
    {
        foreach (var t in tracers) t.LogCheckpoint(taskId, turn, checkpointId, elapsed);
    }

    public void LogBudgetSnapshot(string taskId, int turn, BudgetSnapshot snapshot)
    {
        foreach (var t in tracers) t.LogBudgetSnapshot(taskId, turn, snapshot);
    }
}

[ExcludeFromCodeCoverage]
file sealed class CompositeModelCallScope(IModelCallScope[] scopes) : IModelCallScope
{
    public void Complete(ModelResponse response) { foreach (var s in scopes) s.Complete(response); }
    public void Fail(Exception exception) { foreach (var s in scopes) s.Fail(exception); }
    public void Dispose() { foreach (var s in scopes) s.Dispose(); }
}

[ExcludeFromCodeCoverage]
file sealed class CompositeToolCallScope(IToolCallScope[] scopes) : IToolCallScope
{
    public void Complete(ToolResult result) { foreach (var s in scopes) s.Complete(result); }
    public void Dispose() { foreach (var s in scopes) s.Dispose(); }
}
