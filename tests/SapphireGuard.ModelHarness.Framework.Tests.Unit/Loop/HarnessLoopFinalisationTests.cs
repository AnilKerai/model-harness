using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using Xunit;
using BudgetNs = SapphireGuard.ModelHarness.Framework.Budget;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

/// <summary>
/// The forced-finalisation instruction must reach the model. Every adapter forwards only the first
/// System message, so it has to be folded into that message, not appended as a second one.
/// </summary>
public sealed class HarnessLoopFinalisationTests
{
    private const string Instruction = "Budget is exhausted. Produce your best final answer now";
    private const string SystemPrompt = "You are a careful agent.";

    // ── Budget exhaustion ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_BudgetExhausted_FoldsInstructionIntoTheSingleLeadingSystemMessage()
    {
        var client = new CapturingModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, new SystemPromptContextBuilder(),
            budgetEnforcer: new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        var call = Assert.Single(client.Calls);
        AssertInstructionFoldedIntoLeadingSystemMessage(call.Messages);
        Assert.Empty(call.Tools);
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_KeepsTheOriginalSystemContentAheadOfTheInstruction()
    {
        var client = new CapturingModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, new SystemPromptContextBuilder(),
            budgetEnforcer: new AlwaysExhaustedBudgetEnforcer());

        await harness.RunAsync(NewState("the task"), CancellationToken.None);

        var messages = Assert.Single(client.Calls).Messages;
        var system = messages[0].Content;
        Assert.StartsWith(SystemPrompt, system);
        Assert.True(system.IndexOf(SystemPrompt, StringComparison.Ordinal) < system.IndexOf(Instruction, StringComparison.Ordinal));
        // The conversation after the system message is untouched.
        Assert.Equal([new Message(MessageRole.User, "the task")], messages.Skip(1));
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_ContextBuilderWithoutLeadingSystemMessage_PrependsOneHoldingTheInstruction()
    {
        var client = new CapturingModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, new StubContextBuilder(),
            budgetEnforcer: new AlwaysExhaustedBudgetEnforcer());

        await harness.RunAsync(NewState("the task"), CancellationToken.None);

        var messages = Assert.Single(client.Calls).Messages;
        AssertInstructionFoldedIntoLeadingSystemMessage(messages);
        Assert.StartsWith(Instruction, messages[0].Content);
        Assert.Equal([new Message(MessageRole.User, "the task")], messages.Skip(1));
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_PromptRecordedOnStepAndTracerIsExactlyWhatWasSent()
    {
        var client = new CapturingModelClient(EndTurn("best I can do"));
        var tracer = new PromptCapturingTracer();
        var harness = BuildHarness(client, new SystemPromptContextBuilder(),
            budgetEnforcer: new AlwaysExhaustedBudgetEnforcer(), tracer: tracer);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var sent = Assert.Single(client.Calls).Messages;
        var step = outcome.FinalState.Trajectory.OfType<ModelCallStep>().Single();
        Assert.Equal(sent, step.Prompt);
        Assert.Equal(sent, Assert.Single(tracer.Prompts));
    }

    // ── Intervention cap ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InterventionCapReached_FoldsInstructionIntoTheSingleLeadingSystemMessage()
    {
        // Three consecutive PostModelCall rejections hit the cap; the fourth call is the finalisation.
        var client = new CapturingModelClient(EndTurn("a"), EndTurn("b"), EndTurn("c"), EndTurn("last try"));
        var harness = BuildHarness(client, new SystemPromptContextBuilder(),
            sensors: [new CountdownSensor(HookPoint.PostModelCall, blockCount: 3)]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Contains("intervention limit", outcome.FailureReason);
        Assert.Equal(4, client.Calls.Count);

        // The three ordinary calls never carry the instruction.
        foreach (var ordinary in client.Calls.Take(3))
            Assert.DoesNotContain(ordinary.Messages, m => m.Content.Contains(Instruction));

        var finalisation = client.Calls[3];
        AssertInstructionFoldedIntoLeadingSystemMessage(finalisation.Messages);
        Assert.Empty(finalisation.Tools);
    }

    [Fact]
    public async Task RunAsync_InterventionCapReached_ContextBuilderWithoutLeadingSystemMessage_PrependsOneHoldingTheInstruction()
    {
        var client = new CapturingModelClient(EndTurn("a"), EndTurn("b"), EndTurn("c"), EndTurn("last try"));
        var harness = BuildHarness(client, new StubContextBuilder(),
            sensors: [new CountdownSensor(HookPoint.PostModelCall, blockCount: 3)]);

        await harness.RunAsync(NewState(), CancellationToken.None);

        AssertInstructionFoldedIntoLeadingSystemMessage(client.Calls[3].Messages);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertInstructionFoldedIntoLeadingSystemMessage(IReadOnlyList<Message> messages)
    {
        Assert.Single(messages, m => m.Role == MessageRole.System);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Contains(Instruction, messages[0].Content);
        Assert.Contains("Do not request more tools.", messages[0].Content);
    }

    private static HarnessLoop BuildHarness(
        IModelClient modelClient,
        IContextBuilder contextBuilder,
        IEnumerable<ISensor>? sensors = null,
        BudgetNs.IBudgetEnforcer? budgetEnforcer = null,
        ITracer? tracer = null) =>
        new(modelClient: modelClient,
            toolRegistry: new StubToolRegistry(),
            contextBuilder: contextBuilder,
            sensorRunner: new DefaultSensorRunner(sensors ?? []),
            budgetEnforcer: budgetEnforcer ?? new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: tracer ?? new Fakes.NullTracer(),
            checkpointStore: new NullCheckpointStore());

    private static AgentState NewState(string task = "test task") =>
        AgentState.NewTask(task, new StateBudget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static ModelResponse EndTurn(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };
}

// Returns scripted responses in order and records every prompt + tool list it was sent.
file sealed class CapturingModelClient(params ModelResponse[] responses) : IModelClient
{
    private readonly Queue<ModelResponse> _responses = new(responses);

    public List<(IReadOnlyList<Message> Messages, IReadOnlyList<ToolDefinition> Tools)> Calls { get; } = [];

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        Calls.Add((messages, availableTools));
        return Task.FromResult(_responses.Dequeue());
    }
}

// Mirrors DefaultContextBuilder's output shape: one leading System message, then the conversation.
file sealed class SystemPromptContextBuilder : IContextBuilder
{
    public Task<ContextBuildResult> BuildAsync(AgentState state, IReadOnlyList<ITool> tools, CancellationToken ct) =>
        Task.FromResult(new ContextBuildResult(
            [new Message(MessageRole.System, "You are a careful agent."), new Message(MessageRole.User, state.TaskText)],
            tools));
}

file sealed class PromptCapturingTracer : ITracer
{
    public List<IReadOnlyList<Message>> Prompts { get; } = [];

    public void StartTrace(string taskId, string taskText) { }
    public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools)
    {
        Prompts.Add(prompt);
        return NoopScope.Instance;
    }
    public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) => NoopScope.Instance;
    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) { }
    public void Complete(string taskId, AgentStatus status, string? failureReason) { }
}
