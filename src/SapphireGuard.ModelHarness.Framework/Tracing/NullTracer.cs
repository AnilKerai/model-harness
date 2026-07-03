using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tracing;

[ExcludeFromCodeCoverage]
public sealed class NullTracer : ITracer
{
    public void StartTrace(string taskId, string taskText) { }
    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools) => NoopScope.Instance;
    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) => NoopScope.Instance;
    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) { }
    public void Complete(string taskId, AgentStatus status, string? failureReason) { }
}

/// <summary>Shared no-op scope for tracers that record nothing per model/tool call.</summary>
[ExcludeFromCodeCoverage]
public sealed class NoopScope : IModelCallScope, IToolCallScope
{
    public static readonly NoopScope Instance = new();
    public void Complete(ModelResponse response) { }
    public void Complete(ToolResult result) { }
    public void Dispose() { }
}
