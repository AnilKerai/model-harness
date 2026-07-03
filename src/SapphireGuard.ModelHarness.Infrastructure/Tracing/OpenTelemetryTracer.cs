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
/// Emits a nested span tree aligned with the OpenTelemetry GenAI semantic conventions —
/// an <c>invoke_agent</c> root with <c>chat</c> and <c>execute_tool</c> children — plus the
/// <c>gen_ai.client.token.usage</c> and <c>gen_ai.client.operation.duration</c> metrics, via
/// <see cref="ActivitySource"/> and <see cref="Meter"/>. Wire up your OTel exporters in the
/// host; this class has no OTel SDK dependency. Cost has no GenAI attribute (backends compute
/// it from tokens), so the computed cost is emitted under <c>harness.cost</c>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OpenTelemetryTracer : ITracer, IDisposable
{
    public const string ActivitySourceName = "SapphireGuard.ModelHarness";
    public const string MeterName = "SapphireGuard.ModelHarness";

    private static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<long> TokenUsage = Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per model call, tagged by gen_ai.token.type.");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", unit: "s", description: "Duration of a model call or tool execution.");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "harness.cost", unit: "", description: "Accumulated model call cost (no GenAI equivalent).");
    private static readonly Counter<long> SensorInterventions = Meter.CreateCounter<long>(
        "harness.sensor.interventions", unit: "{intervention}", description: "Number of sensor interventions raised.");

    private readonly ConcurrentDictionary<string, Activity?> _activities = new();

    public void StartTrace(string taskId, string taskText)
    {
        var activity = Source.StartActivity("invoke_agent", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag("harness.task.id", taskId);
        activity?.SetTag("harness.task.text", taskText);
        _activities[taskId] = activity;
    }

    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools)
    {
        var activity = Source.StartActivity("chat", ActivityKind.Client, ParentContext(taskId));
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag("harness.turn", turn);
            activity.SetTag("harness.prompt.messages", prompt.Count);
            activity.SetTag("harness.request.tools", tools.Count);
        }
        return new ModelCallScope(activity);
    }

    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call)
    {
        var activity = Source.StartActivity($"execute_tool {call.ToolName}", ActivityKind.Internal, ParentContext(taskId));
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "execute_tool");
            activity.SetTag("gen_ai.tool.name", call.ToolName);
            activity.SetTag("gen_ai.tool.call.id", call.CallId);
            activity.SetTag("harness.turn", turn);
        }
        return new ToolCallScope(activity, call.ToolName);
    }

    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        if (!result.IsIntervene) return;

        if (_activities.TryGetValue(taskId, out var activity))
        {
            activity?.AddEvent(new ActivityEvent("gen_ai.evaluation.result", tags: new ActivityTagsCollection
            {
                ["harness.turn"] = turn,
                ["harness.sensor.name"] = sensorName,
                ["harness.sensor.hook_point"] = hookPoint.ToString(),
                ["gen_ai.evaluation.explanation"] = result.Reason ?? string.Empty,
            }));
        }

        SensorInterventions.Add(1,
            new TagList { { "harness.sensor.name", sensorName }, { "harness.sensor.hook_point", hookPoint.ToString() } });
    }

    public void LogGuideContribution(string taskId, int turn, string guideName, GuideContribution contribution)
    {
        if (!_activities.TryGetValue(taskId, out var activity)) return;

        activity?.AddEvent(new ActivityEvent("harness.guide.contribution", tags: new ActivityTagsCollection
        {
            ["harness.turn"] = turn,
            ["harness.guide.name"] = guideName,
            ["harness.guide.tools.before"] = contribution.ToolsBefore,
            ["harness.guide.tools.after"] = contribution.ToolsAfter,
            ["harness.guide.tools.removed"] = string.Join(",", contribution.ToolsRemoved),
            ["harness.guide.tools.added"] = string.Join(",", contribution.ToolsAdded),
            ["harness.guide.memory.added"] = contribution.MemorySnippetsAdded,
            ["harness.guide.sections.added"] = contribution.SystemSectionsAdded,
            ["harness.guide.trajectory.added"] = contribution.TrajectoryMessagesAdded,
            ["harness.guide.prompt.char_delta"] = contribution.SystemPromptCharDelta,
        }));
    }

    public void Complete(string taskId, AgentStatus status, string? failureReason)
    {
        if (!_activities.TryRemove(taskId, out var activity) || activity is null) return;

        activity.SetTag("harness.status", status.ToString());
        if (failureReason is not null)
            activity.SetTag("harness.failure.reason", failureReason);

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

    // The root activity is not ambient (StartTrace returned), so children must be parented
    // explicitly by context; default context makes a root span when the task is unknown.
    private ActivityContext ParentContext(string taskId) =>
        _activities.TryGetValue(taskId, out var root) && root is not null ? root.Context : default;

    private static string FinishReason(StopReason stop) => stop switch
    {
        StopReason.EndTurn => "stop",
        StopReason.ToolUse => "tool_calls",
        StopReason.MaxTokens => "length",
        _ => stop.ToString().ToLowerInvariant()
    };

    private sealed class ModelCallScope(Activity? activity) : IModelCallScope
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _completed;

        public void Complete(ModelResponse response)
        {
            _completed = true;
            var provider = response.Provider ?? "unknown";
            var model = response.Model ?? "unknown";

            if (activity is not null)
            {
                if (response.Model is not null)
                {
                    activity.SetTag("gen_ai.request.model", response.Model);
                    activity.DisplayName = $"chat {response.Model}";
                }
                if (response.Provider is not null)
                    activity.SetTag("gen_ai.provider.name", response.Provider);
                activity.SetTag("gen_ai.usage.input_tokens", response.Usage.InputTokens);
                activity.SetTag("gen_ai.usage.output_tokens", response.Usage.OutputTokens);
                activity.SetTag("gen_ai.response.finish_reasons", new[] { FinishReason(response.StopReason) });
                activity.SetTag("harness.response.tool_calls", response.ToolCalls.Count);
                activity.SetTag("harness.cost", (double)response.Cost);
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            var opTags = new TagList
            {
                { "gen_ai.operation.name", "chat" },
                { "gen_ai.provider.name", provider },
                { "gen_ai.request.model", model },
            };
            OperationDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds, opTags);
            Cost.Add((double)response.Cost, opTags);
            TokenUsage.Record(response.Usage.InputTokens, TokenTags(provider, model, "input"));
            TokenUsage.Record(response.Usage.OutputTokens, TokenTags(provider, model, "output"));
        }

        public void Dispose()
        {
            if (activity is null) return;
            if (!_completed)
                activity.SetStatus(ActivityStatusCode.Error, "model call did not complete");
            activity.Dispose();
        }

        private static TagList TokenTags(string provider, string model, string tokenType) => new()
        {
            { "gen_ai.operation.name", "chat" },
            { "gen_ai.provider.name", provider },
            { "gen_ai.request.model", model },
            { "gen_ai.token.type", tokenType },
        };
    }

    private sealed class ToolCallScope(Activity? activity, string toolName) : IToolCallScope
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _completed;

        public void Complete(ToolResult result)
        {
            _completed = true;
            if (activity is not null)
            {
                activity.SetTag("harness.tool.is_error", result.IsError);
                if (result.IsError)
                    activity.SetTag("error.type", "tool_error");
                activity.SetStatus(result.IsError ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
            }

            OperationDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds,
                new TagList { { "gen_ai.operation.name", "execute_tool" }, { "gen_ai.tool.name", toolName } });
        }

        public void Dispose()
        {
            if (activity is null) return;
            if (!_completed)
                activity.SetStatus(ActivityStatusCode.Error, "tool did not complete");
            activity.Dispose();
        }
    }
}
