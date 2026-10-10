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
            ActivityStopped = captured.Add,
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
        // Well-formed JSON, not a raw dump.
        Assert.Equal("assistant", JsonDocument.Parse(output).RootElement.GetProperty("role").GetString());
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
}
