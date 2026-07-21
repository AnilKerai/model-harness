using System.Diagnostics;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
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
}
