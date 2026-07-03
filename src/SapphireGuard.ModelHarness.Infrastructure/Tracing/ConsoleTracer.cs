using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

/// <summary>Writes one JSON line per event to stdout. Cheap, structured, greppable.</summary>
[ExcludeFromCodeCoverage]
public sealed class ConsoleTracer(TimeProvider? timeProvider = null) : ITracer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public void StartTrace(string taskId, string taskText) =>
        Emit(new { evt = "trace_started", taskId, taskText, ts = _time.GetUtcNow() });

    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools) =>
        new ModelCallScope(_time, taskId, turn, prompt.Count, tools.Count);

    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) =>
        new ToolCallScope(_time, taskId, turn, call);

    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) =>
        Emit(new
        {
            evt = "sensor_result",
            taskId,
            turn,
            ts = _time.GetUtcNow(),
            hookPoint = hookPoint.ToString(),
            sensor = sensorName,
            intervene = result.IsIntervene,
            reason = result.Reason
        });

    public void Complete(string taskId, AgentStatus status, string? failureReason) =>
        Emit(new { evt = "trace_completed", taskId, ts = _time.GetUtcNow(), status = status.ToString(), failureReason });

    public void LogGuideContribution(string taskId, int turn, string guideName, GuideContribution contribution) =>
        Emit(new
        {
            evt = "guide_contribution",
            taskId,
            turn,
            ts = _time.GetUtcNow(),
            guide = guideName,
            toolsBefore = contribution.ToolsBefore,
            toolsAfter = contribution.ToolsAfter,
            toolsRemoved = contribution.ToolsRemoved,
            toolsAdded = contribution.ToolsAdded,
            memorySnippetsAdded = contribution.MemorySnippetsAdded,
            systemSectionsAdded = contribution.SystemSectionsAdded,
            trajectoryMessagesAdded = contribution.TrajectoryMessagesAdded,
            systemPromptCharDelta = contribution.SystemPromptCharDelta
        });

    public void LogCompaction(string taskId, int turn, CompactionTrace trace) =>
        Emit(new
        {
            evt = "compaction",
            taskId,
            turn,
            ts = _time.GetUtcNow(),
            stepsEvicted = trace.StepsEvicted,
            tokensReclaimed = trace.TokensReclaimed,
            folded = trace.Folded,
            usage = new { input = trace.Usage.InputTokens, output = trace.Usage.OutputTokens },
            cost = trace.Cost
        });

    private static void Emit(object payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload, Options));

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";

    private sealed class ModelCallScope(TimeProvider time, string taskId, int turn, int promptMessages, int tools) : IModelCallScope
    {
        public void Complete(ModelResponse response) =>
            Emit(new
            {
                evt = "model_call",
                taskId,
                turn,
                ts = time.GetUtcNow(),
                model = response.Model,
                provider = response.Provider,
                promptMessages,
                tools,
                stopReason = response.StopReason.ToString(),
                toolCalls = response.ToolCalls.Count,
                textPreview = Truncate(response.Text, 120),
                usage = new { input = response.Usage.InputTokens, output = response.Usage.OutputTokens },
                cost = response.Cost
            });

        public void Dispose() { }
    }

    private sealed class ToolCallScope(TimeProvider time, string taskId, int turn, ToolCall call) : IToolCallScope
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Complete(ToolResult result) =>
            Emit(new
            {
                evt = "tool_call",
                taskId,
                turn,
                ts = time.GetUtcNow(),
                tool = call.ToolName,
                callId = call.CallId,
                args = call.Arguments.GetRawText(),
                isError = result.IsError,
                resultPreview = Truncate(result.Content, 120),
                durationMs = Stopwatch.GetElapsedTime(_start).TotalMilliseconds
            });

        public void Dispose() { }
    }
}
