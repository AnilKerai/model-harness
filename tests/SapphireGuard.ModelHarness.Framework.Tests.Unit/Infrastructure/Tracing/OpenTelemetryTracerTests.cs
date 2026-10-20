using System.Diagnostics;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tracing;

public sealed class OpenTelemetryTracerTests
{
    private static readonly IReadOnlyList<Message> Prompt = [new Message(MessageRole.User, "secret task detail")];
    private static readonly IReadOnlyList<ToolDefinition> NoTools = [];

    private static ModelResponse Response() => new()
    {
        Text = "secret answer", ToolCalls = [], StopReason = StopReason.EndTurn,
        Usage = Usage.Zero, Cost = 0m, Model = "m", Provider = "p"
    };

    // Runs the action under a fresh listener and returns every activity stopped on the harness source.
    private static List<Activity> Capture(Action action)
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OpenTelemetryTracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (captured) captured.Add(a); }, // other test classes emit on this source in parallel
        };
        ActivitySource.AddActivityListener(listener);
        action();
        return captured;
    }

    private static void DriveOneTurn(OpenTelemetryTracer tracer, string taskId)
    {
        tracer.StartTrace(taskId, "secret task detail");
        var scope = tracer.BeginModelCall(taskId, 0, Prompt, NoTools);
        scope.Complete(Response());
        scope.Dispose();
        tracer.Complete(taskId, AgentStatus.Done, failureReason: null);
    }

    [Fact]
    public void By_default_conversation_content_never_leaves_the_process()
    {
        var taskId = "otel-off-" + Guid.NewGuid().ToString("N");
        var activities = Capture(() => DriveOneTurn(new OpenTelemetryTracer(), taskId));

        var root = activities.Single(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == taskId);
        var chat = activities.Single(a => a.OperationName == "chat" && a.TraceId == root.TraceId);

        Assert.Null(root.GetTagItem("harness.task.text"));
        Assert.Null(chat.GetTagItem("gen_ai.input.messages"));
        Assert.Null(chat.GetTagItem("gen_ai.output.messages"));
        // Operational metadata is still present — only content is gated.
        Assert.Equal(1, chat.GetTagItem("harness.prompt.messages"));
    }

    [Fact]
    public void With_sensitive_data_enabled_content_is_captured()
    {
        var taskId = "otel-on-" + Guid.NewGuid().ToString("N");
        var activities = Capture(() => DriveOneTurn(new OpenTelemetryTracer(enableSensitiveData: true), taskId));

        var root = activities.Single(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == taskId);
        var chat = activities.Single(a => a.OperationName == "chat" && a.TraceId == root.TraceId);

        Assert.Equal("secret task detail", root.GetTagItem("harness.task.text"));

        var input = Assert.IsType<string>(chat.GetTagItem("gen_ai.input.messages"));
        Assert.Contains("secret task detail", input);
        Assert.Contains("\"role\":\"user\"", input);

        var output = Assert.IsType<string>(chat.GetTagItem("gen_ai.output.messages"));
        Assert.Contains("secret answer", output);
        // Well-formed JSON per the GenAI output-messages schema: an array of messages, not a raw dump.
        Assert.Equal("assistant", JsonDocument.Parse(output).RootElement[0].GetProperty("role").GetString());
    }

    [Fact]
    public void Agent_name_is_stamped_on_the_root_span_when_provided()
    {
        var taskId = "otel-name-" + Guid.NewGuid().ToString("N");
        var activities = Capture(() => DriveOneTurn(new OpenTelemetryTracer(agentName: "triage-agent"), taskId));

        var root = activities.Single(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == taskId);
        Assert.Equal("triage-agent", root.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("invoke_agent triage-agent", root.DisplayName);
    }

    // Starts a trace, lets the caller emit events against it, and returns the tags of the one
    // root-span event with the given name.
    private static Dictionary<string, object?> RootEventTags(OpenTelemetryTracer tracer, string eventName, Action<string> emit)
    {
        var taskId = "otel-evt-" + Guid.NewGuid().ToString("N");
        var activities = Capture(() =>
        {
            tracer.StartTrace(taskId, "task");
            emit(taskId);
            tracer.Complete(taskId, AgentStatus.Done, failureReason: null);
        });

        var root = activities.Single(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == taskId);
        return root.Events.Single(e => e.Name == eventName).Tags.ToDictionary(t => t.Key, t => t.Value);
    }

    [Fact]
    public void A_sensor_failure_is_diagnosable_without_enabling_content_capture()
    {
        var tracer = new OpenTelemetryTracer();

        var tags = RootEventTags(tracer, "gen_ai.evaluation.result", taskId =>
            tracer.LogSensorResult(taskId, 0, HookPoint.PreReturn, "critic",
                SensorResult.Failed("critic threw HttpRequestException: 401 Unauthorized")));

        Assert.Equal("sensor_error", tags["error.type"]);
        Assert.Equal("critic threw HttpRequestException: 401 Unauthorized", tags["exception.message"]);
    }

    [Fact]
    public void An_intervention_reason_stays_gated_because_it_can_quote_flagged_content()
    {
        var tracer = new OpenTelemetryTracer();

        var tags = RootEventTags(tracer, "gen_ai.evaluation.result", taskId =>
            tracer.LogSensorResult(taskId, 0, HookPoint.PostModelCall, "pii",
                SensorResult.Intervene("response contained jane@example.com")));

        Assert.DoesNotContain("gen_ai.evaluation.explanation", tags.Keys);
        Assert.DoesNotContain("exception.message", tags.Keys);
    }

    [Fact]
    public void A_failed_compaction_is_flagged_on_the_compact_context_event()
    {
        var tracer = new OpenTelemetryTracer();

        var tags = RootEventTags(tracer, "compact_context", taskId =>
            tracer.LogCompaction(taskId, 0,
                new CompactionTrace(StepsEvicted: 2, TokensReclaimed: 100, Folded: false, Usage.Zero, Cost: 0m)
                {
                    Error = "HttpRequestException: 503 Service Unavailable"
                }));

        Assert.Equal("compaction_error", tags["error.type"]);
        Assert.Equal("HttpRequestException: 503 Service Unavailable", tags["exception.message"]);
    }

    [Fact]
    public void A_successful_compaction_carries_no_error_tags()
    {
        var tracer = new OpenTelemetryTracer();

        var tags = RootEventTags(tracer, "compact_context", taskId =>
            tracer.LogCompaction(taskId, 0,
                new CompactionTrace(StepsEvicted: 2, TokensReclaimed: 100, Folded: true, Usage.Zero, Cost: 0m)));

        Assert.DoesNotContain("error.type", tags.Keys);
        Assert.DoesNotContain("exception.message", tags.Keys);
    }

    // ── Conversation id ──────────────────────────────────────────────────────

    private static ToolCall Call(string name, string argsJson) =>
        new("c1", name, JsonDocument.Parse(argsJson).RootElement);

    private static Activity Root(IEnumerable<Activity> activities, string taskId) =>
        activities.Single(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == taskId);

    [Fact]
    public void Every_span_carries_the_task_id_as_the_conversation_id()
    {
        var taskId = "otel-conv-" + Guid.NewGuid().ToString("N");
        var tracer = new OpenTelemetryTracer();

        var activities = Capture(() =>
        {
            tracer.StartTrace(taskId, "task");
            using (var model = tracer.BeginModelCall(taskId, 0, Prompt, NoTools)) model.Complete(Response());
            using (var tool = tracer.BeginToolCall(taskId, 0, Call("calculator", "{}"))) tool.Complete(new ToolResult("c1", "4"));
            tracer.Complete(taskId, AgentStatus.Done, failureReason: null);
        });

        var trace = activities.Where(a => a.TraceId == Root(activities, taskId).TraceId).ToList();
        Assert.Equal(3, trace.Count);
        // Session grouping keys off this attribute, and some backends read it from every span, not just the root.
        Assert.All(trace, a => Assert.Equal(taskId, a.GetTagItem("gen_ai.conversation.id")));
    }

    [Fact]
    public void A_sub_agent_run_joins_the_conversation_of_the_tool_call_that_started_it()
    {
        var parentId = "otel-parent-" + Guid.NewGuid().ToString("N");
        var childId = "otel-child-" + Guid.NewGuid().ToString("N");
        var parent = new OpenTelemetryTracer();
        var child = new OpenTelemetryTracer();

        var activities = Capture(() =>
        {
            parent.StartTrace(parentId, "delegate the research");
            using (var tool = parent.BeginToolCall(parentId, 0, Call("researcher", "{}")))
            {
                // A sub-agent's loop runs inside the tool span, which BeginToolCall made Activity.Current.
                child.StartTrace(childId, "research");
                using (var model = child.BeginModelCall(childId, 0, Prompt, NoTools)) model.Complete(Response());
                child.Complete(childId, AgentStatus.Done, failureReason: null);
                tool.Complete(new ToolResult("c1", "found it"));
            }
            parent.Complete(parentId, AgentStatus.Done, failureReason: null);
        });

        var childRoot = Root(activities, childId);
        Assert.Equal(Root(activities, parentId).TraceId, childRoot.TraceId);
        Assert.All(activities.Where(a => a.TraceId == childRoot.TraceId),
            a => Assert.Equal(parentId, a.GetTagItem("gen_ai.conversation.id")));
    }

    // ── Run input / output on the root ───────────────────────────────────────

    private static AgentOutcome SecondTurnOutcome(string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        var budget = new Framework.State.Budget
        {
            MaxTurns = 5, MaxTotalTokens = 1_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromMinutes(1)
        };
        var answer = Response() with { Text = "the second answer" };
        var state = AgentState.NewTask("the opener", budget, now, taskId: taskId)
            .WithUserMessage("the second question", now)
            .AppendStep(new ModelCallStep(Guid.NewGuid(), now, Prompt, answer, Usage.Zero, 0m));
        return new AgentOutcome
        {
            TaskId = taskId, Status = AgentStatus.Done, FinalAnswer = answer.Text, FinalState = state with { Status = AgentStatus.Done }
        };
    }

    private static Activity RunWithOutcome(OpenTelemetryTracer tracer, AgentOutcome outcome)
    {
        var activities = Capture(() =>
        {
            tracer.StartTrace(outcome.TaskId, "the opener");
            tracer.LogOutcome(outcome);
            tracer.Complete(outcome.TaskId, outcome.Status, failureReason: null);
        });
        return Root(activities, outcome.TaskId);
    }

    [Fact]
    public void The_root_records_the_current_turn_and_the_final_answer_as_the_run_input_and_output()
    {
        var root = RunWithOutcome(new OpenTelemetryTracer(enableSensitiveData: true),
            SecondTurnOutcome("otel-io-" + Guid.NewGuid().ToString("N")));

        var input = JsonDocument.Parse(Assert.IsType<string>(root.GetTagItem("gen_ai.input.messages"))).RootElement;
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        // The latest user message, not the conversation opener held in TaskText.
        Assert.Equal("the second question", input[0].GetProperty("parts")[0].GetProperty("content").GetString());

        var output = JsonDocument.Parse(Assert.IsType<string>(root.GetTagItem("gen_ai.output.messages"))).RootElement;
        Assert.Equal("the second answer", output[0].GetProperty("parts")[0].GetProperty("content").GetString());
        Assert.Equal("stop", output[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void The_run_input_and_output_stay_off_the_root_by_default()
    {
        var root = RunWithOutcome(new OpenTelemetryTracer(), SecondTurnOutcome("otel-io-off-" + Guid.NewGuid().ToString("N")));

        Assert.Null(root.GetTagItem("gen_ai.input.messages"));
        Assert.Null(root.GetTagItem("gen_ai.output.messages"));
    }

    // ── Message schema ───────────────────────────────────────────────────────

    [Fact]
    public void Prompt_tool_calls_and_results_become_typed_parts_on_merged_messages()
    {
        var taskId = "otel-parts-" + Guid.NewGuid().ToString("N");
        var tracer = new OpenTelemetryTracer(enableSensitiveData: true);
        IReadOnlyList<Message> prompt =
        [
            new(MessageRole.System, "be precise"),
            new(MessageRole.User, "what is 124 * 37?"),
            new(MessageRole.Assistant, "I'll use the calculator."),
            new(MessageRole.ToolUse, """[tool_call name=calculator id=c1] {"op":"mul","lhs":124,"rhs":37}"""),
            new(MessageRole.Tool, "[tool_result id=c1 error=False] 4588"),
        ];

        var activities = Capture(() =>
        {
            tracer.StartTrace(taskId, "task");
            using (var model = tracer.BeginModelCall(taskId, 0, prompt, NoTools)) model.Complete(Response());
            tracer.Complete(taskId, AgentStatus.Done, failureReason: null);
        });

        var chat = activities.Single(a => a.OperationName == "chat" && a.TraceId == Root(activities, taskId).TraceId);
        var messages = JsonDocument.Parse(Assert.IsType<string>(chat.GetTagItem("gen_ai.input.messages"))).RootElement;

        Assert.Equal(["system", "user", "assistant", "tool"],
            messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        // The assistant's text and its tool call travel as one message, as the adapters send them.
        var assistant = messages[2].GetProperty("parts");
        Assert.Equal("text", assistant[0].GetProperty("type").GetString());
        Assert.Equal("tool_call", assistant[1].GetProperty("type").GetString());
        Assert.Equal("c1", assistant[1].GetProperty("id").GetString());
        Assert.Equal("calculator", assistant[1].GetProperty("name").GetString());
        Assert.Equal(124, assistant[1].GetProperty("arguments").GetProperty("lhs").GetInt32());

        var result = messages[3].GetProperty("parts")[0];
        Assert.Equal("tool_call_response", result.GetProperty("type").GetString());
        Assert.Equal("c1", result.GetProperty("id").GetString());
        Assert.Equal("4588", result.GetProperty("response").GetString());
    }

    [Fact]
    public void A_tool_calling_response_is_an_output_message_with_tool_call_parts()
    {
        var taskId = "otel-out-" + Guid.NewGuid().ToString("N");
        var tracer = new OpenTelemetryTracer(enableSensitiveData: true);
        var response = Response() with
        {
            Text = null, StopReason = StopReason.ToolUse, ToolCalls = [Call("calculator", """{"op":"mul"}""")]
        };

        var activities = Capture(() =>
        {
            tracer.StartTrace(taskId, "task");
            using (var model = tracer.BeginModelCall(taskId, 0, Prompt, NoTools)) model.Complete(response);
            tracer.Complete(taskId, AgentStatus.Done, failureReason: null);
        });

        var chat = activities.Single(a => a.OperationName == "chat" && a.TraceId == Root(activities, taskId).TraceId);
        var message = JsonDocument.Parse(Assert.IsType<string>(chat.GetTagItem("gen_ai.output.messages"))).RootElement[0];

        Assert.Equal("tool_call", message.GetProperty("finish_reason").GetString());
        var part = Assert.Single(message.GetProperty("parts").EnumerateArray());
        Assert.Equal("tool_call", part.GetProperty("type").GetString());
        Assert.Equal("mul", part.GetProperty("arguments").GetProperty("op").GetString());
    }

    // ── Root span status ─────────────────────────────────────────────────────

    private static Activity RootEndedWith(AgentStatus status, string? failureReason)
    {
        var taskId = "otel-status-" + Guid.NewGuid().ToString("N");
        var tracer = new OpenTelemetryTracer();

        var activities = Capture(() =>
        {
            tracer.StartTrace(taskId, "task");
            tracer.Complete(taskId, status, failureReason);
        });

        return Root(activities, taskId);
    }

    [Theory]
    [InlineData(AgentStatus.Done, ActivityStatusCode.Ok, null)]
    [InlineData(AgentStatus.Failed, ActivityStatusCode.Error, "boom")]
    [InlineData(AgentStatus.PartialResult, ActivityStatusCode.Error, "budget")]
    public void The_root_span_status_follows_how_the_run_ended(AgentStatus status, ActivityStatusCode expected, string? failureReason)
    {
        var root = RootEndedWith(status, failureReason);

        Assert.Equal(expected, root.Status);
        Assert.Equal(status.ToString(), root.GetTagItem("harness.status"));
    }

    [Fact]
    public void A_run_suspended_for_human_input_is_not_an_error()
    {
        var root = RootEndedWith(AgentStatus.AwaitingHuman, failureReason: null);

        // Unset, not Error: a suspension is a normal wait and must stay out of error-rate dashboards and alerts.
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
        Assert.Equal("AwaitingHuman", root.GetTagItem("harness.status"));
    }
}
