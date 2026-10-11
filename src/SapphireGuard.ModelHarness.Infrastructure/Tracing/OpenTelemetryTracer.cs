using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

/// <summary>
/// Emits a nested span tree aligned with the OpenTelemetry GenAI semantic conventions —
/// an <c>invoke_agent</c> root with <c>chat</c> and <c>execute_tool</c> children — plus the
/// <c>gen_ai.client.token.usage</c> and <c>gen_ai.client.operation.duration</c> metrics, via
/// <see cref="ActivitySource"/> and <see cref="Meter"/>. This class has no OTel SDK dependency:
/// the host must register <see cref="ActivitySourceName"/> with its tracer provider (<c>AddSource</c>)
/// and <see cref="MeterName"/> with its meter provider (<c>AddMeter</c>), then wire an exporter —
/// without the registration <c>StartActivity</c> returns null and nothing is emitted, exporter or not.
/// Cost has no GenAI attribute (backends compute it from tokens), so the computed cost is emitted
/// under <c>harness.cost</c>.
/// <para><paramref name="enableSensitiveData"/> (default <see langword="false"/>) gates capture of
/// <em>conversation content</em> — the task text, prompt/response message bodies, tool arguments and
/// results, and sensor-reason free text. Off by default so a run never leaks user or model content to
/// a telemetry backend; turn it on in development to see the actual messages. Error-path diagnostics
/// (span status, the <c>exception</c> event, <c>harness.failure.reason</c>) are always emitted — they
/// only appear when something already failed, and hiding them would defeat the point of tracing.</para>
/// <para><paramref name="agentName"/> (optional) is stamped on the root span as
/// <c>gen_ai.agent.name</c> and into its display name, so multiple agents in one process are
/// distinguishable in a backend. Leave null for a single-agent host (use <c>service.name</c> to
/// identify the deployment).</para>
/// <para>Every span carries <c>gen_ai.conversation.id</c> — the task id, which stays the same across chat
/// turns and HITL resumes, so a backend can group a conversation's runs (one trace per <c>RunAsync</c>) into
/// a session. A sub-agent run nested under a parent's tool span inherits the parent's conversation id.
/// With content capture on, the root span also carries the run's input (the user message that started it)
/// and final answer as <c>gen_ai.input.messages</c> / <c>gen_ai.output.messages</c>, and every message
/// attribute follows the GenAI input/output messages JSON schema (role + typed parts).</para>
/// </summary>
/// <param name="enableSensitiveData">Capture conversation content (see remarks). Default <see langword="false"/>.</param>
/// <param name="agentName">Optional agent name for <c>gen_ai.agent.name</c> on the root span.</param>
[ExcludeFromCodeCoverage]
public sealed class OpenTelemetryTracer(bool enableSensitiveData = false, string? agentName = null) : ITracer, IDisposable
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
    private static readonly Histogram<long> CompactionReclaimed = Meter.CreateHistogram<long>(
        "harness.compaction.tokens_reclaimed", unit: "{token}", description: "Tokens reclaimed per compaction pass.");
    private static readonly Counter<long> SensorEvaluations = Meter.CreateCounter<long>(
        "harness.sensor.evaluations", unit: "{evaluation}", description: "Sensor evaluations, tagged by verdict (pass/intervene/error).");
    private static readonly Histogram<double> RateLimitWait = Meter.CreateHistogram<double>(
        "harness.ratelimit.wait", unit: "s", description: "Duration the loop waited on a rate-limit backoff before a turn.");
    private static readonly Histogram<double> CheckpointDuration = Meter.CreateHistogram<double>(
        "harness.checkpoint.duration", unit: "s", description: "Duration of a per-turn checkpoint save.");

    private readonly ConcurrentDictionary<string, Activity?> _activities = new();

    private const string ConversationIdTag = "gen_ai.conversation.id";

    private static readonly JsonSerializerOptions SemconvJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public void StartTrace(string taskId, string taskText)
    {
        // Read before StartActivity replaces Activity.Current: a sub-agent run starts inside its parent's
        // execute_tool span and joins that conversation, so a backend that keys sessions off this
        // attribute sees one value per trace rather than the parent's and the sub-agent's competing.
        var conversationId = Activity.Current?.GetTagItem(ConversationIdTag) as string ?? taskId;
        var activity = Source.StartActivity("invoke_agent", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag(ConversationIdTag, conversationId);
        if (agentName is not null && activity is not null)
        {
            activity.SetTag("gen_ai.agent.name", agentName);
            activity.DisplayName = $"invoke_agent {agentName}";
        }
        activity?.SetTag("harness.task.id", taskId);
        if (enableSensitiveData)
            activity?.SetTag("harness.task.text", taskText);
        _activities[taskId] = activity;
    }

    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools)
    {
        var activity = Source.StartActivity("chat", ActivityKind.Client, ParentContext(taskId));
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag(ConversationIdTag, ConversationId(taskId));
            activity.SetTag("harness.turn", turn);
            activity.SetTag("harness.prompt.messages", prompt.Count);
            activity.SetTag("harness.request.tools", tools.Count);
            if (enableSensitiveData)
                activity.SetTag("gen_ai.input.messages", SerializeMessages(prompt));
        }
        return new ModelCallScope(activity, enableSensitiveData);
    }

    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call)
    {
        var activity = Source.StartActivity($"execute_tool {call.ToolName}", ActivityKind.Internal, ParentContext(taskId));
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "execute_tool");
            activity.SetTag(ConversationIdTag, ConversationId(taskId));
            activity.SetTag("gen_ai.tool.name", call.ToolName);
            activity.SetTag("gen_ai.tool.call.id", call.CallId);
            activity.SetTag("harness.turn", turn);
            if (enableSensitiveData)
                activity.SetTag("gen_ai.tool.call.arguments", call.Arguments.GetRawText());
        }
        return new ToolCallScope(activity, call.ToolName, enableSensitiveData);
    }

    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result)
    {
        // Every evaluation (pass included) is counted so full sensor activity is visible in metrics;
        // only interventions/errors become span events, to keep the root span from bloating on passes.
        SensorEvaluations.Add(1, new TagList
        {
            { "harness.sensor.name", sensorName },
            { "harness.sensor.hook_point", hookPoint.ToString() },
            { "harness.sensor.verdict", result.IsError ? "error" : result.IsIntervene ? "intervene" : "pass" },
        });

        if (!result.IsIntervene && !result.IsError) return;

        if (_activities.TryGetValue(taskId, out var activity))
        {
            var tags = new ActivityTagsCollection
            {
                ["harness.turn"] = turn,
                ["harness.sensor.name"] = sensorName,
                ["harness.sensor.hook_point"] = hookPoint.ToString(),
            };
            // The reason can quote the very content the sensor flagged (a PII detector names the PII),
            // so it is conversation content — gated. The sensor name/hook_point/verdict stay always-on.
            if (enableSensitiveData)
                tags["gen_ai.evaluation.explanation"] = result.Reason ?? string.Empty;
            if (result.IsError)
            {
                // An error's reason describes the failure — the runner's exception summary, or a
                // sensor's own Failed reason — not content the sensor flagged, so it is emitted ungated
                // like a guide error: a failing sensor has to be diagnosable without content capture.
                tags["error.type"] = "sensor_error";
                tags["exception.message"] = result.Reason ?? string.Empty;
            }
            activity?.AddEvent(new ActivityEvent("gen_ai.evaluation.result", tags: tags));
        }

        // A sensor error is not an intervention — don't inflate the count.
        if (result.IsIntervene)
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

    public void LogGuideError(string taskId, int turn, string guideName, string error)
    {
        if (!_activities.TryGetValue(taskId, out var activity)) return;

        activity?.AddEvent(new ActivityEvent("harness.guide.error", tags: new ActivityTagsCollection
        {
            ["harness.turn"] = turn,
            ["harness.guide.name"] = guideName,
            ["error.type"] = "guide_error",
            ["exception.message"] = error,
        }));
    }

    public void LogCompaction(string taskId, int turn, CompactionTrace trace)
    {
        if (!_activities.TryGetValue(taskId, out var activity)) return;

        var tags = new ActivityTagsCollection
        {
            ["harness.turn"] = turn,
            ["harness.compaction.steps_evicted"] = trace.StepsEvicted,
            ["harness.compaction.tokens_reclaimed"] = trace.TokensReclaimed,
            ["harness.compaction.folded"] = trace.Folded,
            ["harness.compaction.usage.input_tokens"] = trace.Usage.InputTokens,
            ["harness.compaction.usage.output_tokens"] = trace.Usage.OutputTokens,
            ["harness.cost"] = (double)trace.Cost,
        };
        if (trace.Error is { } error)
        {
            tags["error.type"] = "compaction_error";
            tags["exception.message"] = error;
        }
        activity?.AddEvent(new ActivityEvent("compact_context", tags: tags));

        CompactionReclaimed.Record(trace.TokensReclaimed, new TagList { { "harness.compaction.folded", trace.Folded } });
    }

    public void LogRateLimit(string taskId, int turn, TimeSpan delay)
    {
        if (_activities.TryGetValue(taskId, out var activity))
            activity?.AddEvent(new ActivityEvent("harness.rate_limit", tags: new ActivityTagsCollection
            {
                ["harness.turn"] = turn,
                ["harness.ratelimit.delay_seconds"] = delay.TotalSeconds,
            }));
        RateLimitWait.Record(delay.TotalSeconds, new TagList { { "harness.turn", turn } });
    }

    public void LogCheckpoint(string taskId, int turn, string checkpointId, TimeSpan elapsed)
    {
        if (_activities.TryGetValue(taskId, out var activity))
            activity?.AddEvent(new ActivityEvent("harness.checkpoint", tags: new ActivityTagsCollection
            {
                ["harness.turn"] = turn,
                ["harness.checkpoint.id"] = checkpointId,
                ["harness.checkpoint.duration_seconds"] = elapsed.TotalSeconds,
            }));
        CheckpointDuration.Record(elapsed.TotalSeconds, new TagList { { "harness.turn", turn } });
    }

    public void LogBudgetSnapshot(string taskId, int turn, BudgetSnapshot snapshot)
    {
        if (!_activities.TryGetValue(taskId, out var activity)) return;
        activity?.AddEvent(new ActivityEvent("harness.budget", tags: new ActivityTagsCollection
        {
            ["harness.turn"] = turn,
            ["harness.budget.turns_used"] = snapshot.TurnsUsed,
            ["harness.budget.turns_max"] = snapshot.MaxTurns,
            ["harness.budget.tokens_used"] = snapshot.TokensUsed,
            ["harness.budget.tokens_max"] = snapshot.MaxTotalTokens,
            ["harness.budget.cost_used"] = (double)snapshot.CostUsed,
            ["harness.budget.cost_max"] = (double)snapshot.MaxCost,
            ["harness.budget.wallclock_used_seconds"] = snapshot.Elapsed.TotalSeconds,
            ["harness.budget.wallclock_max_seconds"] = snapshot.MaxWallClock.TotalSeconds,
        }));
    }

    public void LogOutcome(AgentOutcome outcome)
    {
        if (!enableSensitiveData || !_activities.TryGetValue(outcome.TaskId, out var activity) || activity is null) return;

        // The run's input is the user message that started it — in a chat, the current turn, not the opener
        // the frozen TaskText holds — so a backend's trace view reads as one exchange per run.
        if (outcome.FinalState.Trajectory.OfType<UserMessageStep>().LastOrDefault() is { } input)
            activity.SetTag("gen_ai.input.messages", JsonSerializer.Serialize(
                new[] { new ChatMessage("user", [new TextPart(input.Content)]) }, SemconvJson));

        if (outcome.FinalAnswer is { } answer)
        {
            // The answer is the last response's text, so its stop reason is the answer's finish reason.
            var stop = outcome.FinalState.Trajectory.OfType<ModelCallStep>().LastOrDefault()?.Response.StopReason;
            activity.SetTag("gen_ai.output.messages", JsonSerializer.Serialize(
                new[] { new OutputMessage("assistant", [new TextPart(answer)], MessageFinishReason(stop ?? StopReason.EndTurn)) },
                SemconvJson));
        }
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

    private string ConversationId(string taskId) =>
        _activities.TryGetValue(taskId, out var root) && root?.GetTagItem(ConversationIdTag) is string id ? id : taskId;

    private static string FinishReason(StopReason stop) => stop switch
    {
        StopReason.EndTurn => "stop",
        StopReason.ToolUse => "tool_calls",
        StopReason.MaxTokens => "length",
        StopReason.ContentFilter => "content_filter",
        StopReason.Refusal => "refusal",
        StopReason.ContextWindowExceeded => "model_context_window_exceeded",
        _ => stop.ToString().ToLowerInvariant()
    };

    // gen_ai.output.messages restricts finish_reason to this enum; gen_ai.response.finish_reasons (above) is
    // free-form and keeps its existing values.
    private static string MessageFinishReason(StopReason stop) => stop switch
    {
        StopReason.EndTurn or StopReason.StopSequence => "stop",
        StopReason.ToolUse => "tool_call",
        StopReason.MaxTokens or StopReason.ContextWindowExceeded => "length",
        StopReason.ContentFilter or StopReason.Refusal => "content_filter",
        _ => "error"
    };

    // gen_ai.input.messages per the GenAI input-messages JSON schema. Tool calls and results arrive as the text
    // HeadEvictionTrajectoryGuide renders, so they are parsed back into typed parts (a backend then shows them as
    // tool calls, not prose), and adjacent messages with the same role merge into one — as the adapters send them.
    private static string SerializeMessages(IReadOnlyList<Message> messages)
    {
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var (role, part) = ToPart(message);
            if (result.Count > 0 && result[^1].Role == role)
                result[^1].Parts.Add(part);
            else
                result.Add(new ChatMessage(role, [part]));
        }
        return JsonSerializer.Serialize(result, SemconvJson);
    }

    private static string SerializeResponse(ModelResponse response)
    {
        var parts = new List<object>();
        if (!string.IsNullOrEmpty(response.Text))
            parts.Add(new TextPart(response.Text));
        parts.AddRange(response.ToolCalls.Select(tc => new ToolCallPart(tc.CallId, tc.ToolName, tc.Arguments)));
        return JsonSerializer.Serialize(
            new[] { new OutputMessage("assistant", parts, MessageFinishReason(response.StopReason)) }, SemconvJson);
    }

    private static (string Role, object Part) ToPart(Message message) => message.Role switch
    {
        MessageRole.ToolUse => ("assistant", ParseToolCall(message.Content)),
        MessageRole.Tool => ("tool", ParseToolResult(message.Content)),
        _ => (message.Role.ToString().ToLowerInvariant(), new TextPart(message.Content))
    };

    // "[tool_call name={name} id={callId}] {argsJson}" — the format HeadEvictionTrajectoryGuide writes.
    private static object ParseToolCall(string content)
    {
        if (!TrySplitHeader(content, "[tool_call ", out var meta, out var body))
            return new TextPart(content);
        object? arguments;
        try
        {
            using var doc = JsonDocument.Parse(body);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            arguments = body;
        }
        return new ToolCallPart(meta.GetValueOrDefault("id"), meta.GetValueOrDefault("name") ?? "", arguments);
    }

    // "[tool_result id={callId} error={bool}] {resultText}" — the format HeadEvictionTrajectoryGuide writes.
    private static object ParseToolResult(string content) =>
        TrySplitHeader(content, "[tool_result ", out var meta, out var body)
            ? new ToolCallResponsePart(meta.GetValueOrDefault("id"), body)
            : new TextPart(content);

    private static bool TrySplitHeader(string content, string prefix, out Dictionary<string, string> meta, out string body)
    {
        meta = [];
        body = content;
        var close = content.IndexOf(']');
        if (!content.StartsWith(prefix, StringComparison.Ordinal) || close < 0)
            return false;
        foreach (var pair in content[prefix.Length..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var key, var value])
                meta[key] = value;
        body = content[(close + 1)..].TrimStart();
        return true;
    }

    private sealed record ChatMessage(string Role, List<object> Parts);
    private sealed record OutputMessage(string Role, List<object> Parts, string FinishReason);
    private sealed record TextPart(string Content) { [JsonPropertyOrder(-1)] public string Type => "text"; }
    private sealed record ToolCallPart(string? Id, string Name, object? Arguments) { [JsonPropertyOrder(-1)] public string Type => "tool_call"; }
    private sealed record ToolCallResponsePart(string? Id, string Response) { [JsonPropertyOrder(-1)] public string Type => "tool_call_response"; }

    private sealed class ModelCallScope(Activity? activity, bool enableSensitiveData) : IModelCallScope
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
                if (response.CachedInputTokens > 0)
                    activity.SetTag("gen_ai.usage.cache_read_input_tokens", response.CachedInputTokens);
                if (response.CacheWriteTokens > 0)
                    activity.SetTag("gen_ai.usage.cache_creation_input_tokens", response.CacheWriteTokens);
                activity.SetTag("gen_ai.response.finish_reasons", new[] { FinishReason(response.StopReason) });
                activity.SetTag("harness.response.tool_calls", response.ToolCalls.Count);
                activity.SetTag("harness.cost", (double)response.Cost);
                if (enableSensitiveData)
                    activity.SetTag("gen_ai.output.messages", SerializeResponse(response));
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
            // Report cache read/creation as their own token types so the "input" bucket stays the uncached
            // tokens and hit-rate = cache_read / (input + cache_read + cache_creation) — the buckets are disjoint.
            var uncachedInput = Math.Max(0, response.Usage.InputTokens - response.CachedInputTokens - response.CacheWriteTokens);
            TokenUsage.Record(uncachedInput, TokenTags(provider, model, "input"));
            if (response.CachedInputTokens > 0)
                TokenUsage.Record(response.CachedInputTokens, TokenTags(provider, model, "cache_read"));
            if (response.CacheWriteTokens > 0)
                TokenUsage.Record(response.CacheWriteTokens, TokenTags(provider, model, "cache_creation"));
            TokenUsage.Record(response.Usage.OutputTokens, TokenTags(provider, model, "output"));
        }

        public void Fail(Exception exception)
        {
            _completed = true; // finalised — keep Dispose from overwriting with the generic message
            if (activity is null) return;
            // Standard OTel exception event (Activity.AddException is .NET 9+, but this project also targets net8.0).
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.message"] = exception.Message,
                ["exception.stacktrace"] = exception.ToString(),
            }));
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
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

    private sealed class ToolCallScope(Activity? activity, string toolName, bool enableSensitiveData) : IToolCallScope
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
                if (enableSensitiveData)
                    activity.SetTag("gen_ai.tool.call.result", result.Content);
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
