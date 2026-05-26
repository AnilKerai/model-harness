using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

/// <summary>Writes one JSON line per event to stdout. Cheap, structured, greppable.</summary>
[ExcludeFromCodeCoverage]
public sealed class ConsoleTracer : ITracer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public void StartTrace(string taskId, string taskText) =>
        Emit(new { evt = "trace_started", taskId, taskText, ts = DateTimeOffset.UtcNow });

    public void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response) =>
        Emit(new
        {
            evt = "model_call",
            taskId,
            ts = DateTimeOffset.UtcNow,
            promptMessages = prompt.Count,
            tools = tools.Count,
            stopReason = response.StopReason.ToString(),
            toolCalls = response.ToolCalls.Count,
            textPreview = Truncate(response.Text, 120),
            usage = new { input = response.Usage.InputTokens, output = response.Usage.OutputTokens },
            cost = response.Cost
        });

    public void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration) =>
        Emit(new
        {
            evt = "tool_call",
            taskId,
            ts = DateTimeOffset.UtcNow,
            tool = call.ToolName,
            callId = call.CallId,
            args = call.Arguments.GetRawText(),
            isError = result.IsError,
            resultPreview = Truncate(result.Content, 120),
            durationMs = duration.TotalMilliseconds
        });

    public void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result) =>
        Emit(new
        {
            evt = "sensor_result",
            taskId,
            ts = DateTimeOffset.UtcNow,
            hookPoint = hookPoint.ToString(),
            sensor = sensorName,
            intervene = result.IsIntervene,
            reason = result.Reason
        });

    public void Complete(string taskId, AgentStatus status, string? failureReason) =>
        Emit(new { evt = "trace_completed", taskId, ts = DateTimeOffset.UtcNow, status = status.ToString(), failureReason });

    private static void Emit(object payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload, Options));

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";
}
