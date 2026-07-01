using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

/// <summary>
/// Emits traces and metrics via System.Diagnostics.ActivitySource and
/// System.Diagnostics.Metrics.Meter — the standard .NET observability hooks.
/// Wire up your OTel exporters in the host; this class has no OTel SDK dependency.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OpenTelemetryTracer : ITracer, IDisposable
{
    public const string ActivitySourceName = "SapphireGuard.ModelHarness";
    public const string MeterName = "SapphireGuard.ModelHarness";

    private static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>(
        "agent.tokens.input", unit: "{token}", description: "Total input tokens consumed.");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>(
        "agent.tokens.output", unit: "{token}", description: "Total output tokens produced.");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "agent.cost", unit: "", description: "Accumulated model call cost.");
    private static readonly Histogram<double> ToolDurationMs = Meter.CreateHistogram<double>(
        "agent.tool.duration", unit: "ms", description: "Tool execution duration.");
    private static readonly Counter<long> SensorInterventions = Meter.CreateCounter<long>(
        "agent.sensor.interventions", unit: "{intervention}", description: "Number of sensor interventions raised.");

    private readonly ConcurrentDictionary<string, Activity?> _activities = new();

    public void StartTrace(string taskId, string taskText)
    {
        var activity = Source.StartActivity("agent.task", ActivityKind.Internal);
        activity?.SetTag("agent.task.id", taskId);
        activity?.SetTag("agent.task.text", taskText);
        _activities[taskId] = activity;
    }

    public void LogModelCall(string taskId, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools, ModelResponse response)
    {
        if (_activities.TryGetValue(taskId, out var activity))
        {
            activity?.AddEvent(new ActivityEvent("model_call", tags: new ActivityTagsCollection
            {
                ["prompt.messages"] = prompt.Count,
                ["tools.count"] = tools.Count,
                ["stop.reason"] = response.StopReason.ToString(),
                ["tool.calls"] = response.ToolCalls.Count,
                ["tokens.input"] = response.Usage.InputTokens,
                ["tokens.output"] = response.Usage.OutputTokens,
                ["cost"] = (double)response.Cost,
            }));
        }

        var tags = new TagList { { "task.id", taskId } };
        InputTokens.Add(response.Usage.InputTokens, tags);
        OutputTokens.Add(response.Usage.OutputTokens, tags);
        Cost.Add((double)response.Cost, tags);
    }

    public void LogToolCall(string taskId, ToolCall call, ToolResult result, TimeSpan duration)
    {
        if (_activities.TryGetValue(taskId, out var activity))
        {
            activity?.AddEvent(new ActivityEvent("tool_call", tags: new ActivityTagsCollection
            {
                ["tool.name"] = call.ToolName,
                ["tool.call.id"] = call.CallId,
                ["tool.is_error"] = result.IsError,
                ["tool.duration.ms"] = duration.TotalMilliseconds,
            }));
        }

        ToolDurationMs.Record(duration.TotalMilliseconds,
            new TagList { { "tool.name", call.ToolName }, { "task.id", taskId } });
    }

    public void LogSensorResult(string taskId, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        if (!result.IsIntervene) return;

        if (_activities.TryGetValue(taskId, out var activity))
        {
            activity?.AddEvent(new ActivityEvent("sensor_intervention", tags: new ActivityTagsCollection
            {
                ["sensor.name"] = sensorName,
                ["hook.point"] = hookPoint.ToString(),
                ["sensor.reason"] = result.Reason ?? string.Empty,
            }));
        }

        SensorInterventions.Add(1,
            new TagList { { "sensor.name", sensorName }, { "hook.point", hookPoint.ToString() }, { "task.id", taskId } });
    }

    public void LogGuideContribution(string taskId, string guideName, GuideContribution contribution)
    {
        if (!_activities.TryGetValue(taskId, out var activity)) return;

        activity?.AddEvent(new ActivityEvent("guide_contribution", tags: new ActivityTagsCollection
        {
            ["guide.name"] = guideName,
            ["guide.tools.before"] = contribution.ToolsBefore,
            ["guide.tools.after"] = contribution.ToolsAfter,
            ["guide.tools.removed"] = string.Join(",", contribution.ToolsRemoved),
            ["guide.tools.added"] = string.Join(",", contribution.ToolsAdded),
            ["guide.memory.added"] = contribution.MemorySnippetsAdded,
            ["guide.sections.added"] = contribution.SystemSectionsAdded,
            ["guide.trajectory.added"] = contribution.TrajectoryMessagesAdded,
            ["guide.prompt.char_delta"] = contribution.SystemPromptCharDelta,
        }));
    }

    public void Complete(string taskId, AgentStatus status, string? failureReason)
    {
        if (!_activities.TryRemove(taskId, out var activity) || activity is null) return;

        activity.SetTag("agent.status", status.ToString());
        if (failureReason is not null)
            activity.SetTag("agent.failure.reason", failureReason);

        activity.SetStatus(
            status == AgentStatus.Done ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
            failureReason);

        activity.Dispose();
    }

    public void Dispose()
    {
        foreach (var activity in _activities.Values)
            activity?.Dispose();
        _activities.Clear();
    }
}
