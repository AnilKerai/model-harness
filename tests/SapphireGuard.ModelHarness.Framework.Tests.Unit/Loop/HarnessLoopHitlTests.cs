using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

public sealed class HarnessLoopHitlTests
{
    private static HarnessLoop BuildHarness(ScriptedModelClient modelClient, StubToolRegistry? toolRegistry = null) =>
        new(
            modelClient: modelClient,
            toolRegistry: toolRegistry ?? new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

    private static AgentState NewState() =>
        AgentState.NewTask("test task", new SapphireGuard.ModelHarness.Framework.State.Budget
        {
            MaxTurns = 10,
            MaxContextTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });

    private static ModelResponse EndTurnResponse(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ModelResponse ToolUseResponse(params ToolCall[] calls) => new()
    {
        Text = null,
        ToolCalls = calls,
        StopReason = StopReason.ToolUse,
        Usage = Usage.Zero,
        Cost = 0m
    };

    // ── Suspension ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolReturnsPending_ReturnsAwaitingHuman()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your name?", IsPending: true));
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.NotNull(outcome.PendingHumanInput);
        Assert.Equal(callId, outcome.PendingHumanInput.CallId);
        Assert.Equal("What is your name?", outcome.PendingHumanInput.Question);
    }

    [Fact]
    public async Task RunAsync_ToolReturnsPending_PendingStepInTrajectory()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "Approve this action?", IsPending: true));
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var step = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.True(step.Result.IsPending);
        Assert.Equal("Approve this action?", step.Result.Content);
    }

    [Fact]
    public async Task RunAsync_ToolReturnsPending_FinalStateCarriesPendingHumanInput()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your age?", IsPending: true));
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.FinalState.Status);
        Assert.NotNull(outcome.FinalState.PendingHumanInput);
        Assert.Equal(callId, outcome.FinalState.PendingHumanInput.CallId);
        Assert.Equal("What is your age?", outcome.FinalState.PendingHumanInput.Question);
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ResumedAfterHumanAnswer_CompletesToDone()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your name?", IsPending: true));
        var firstClient = new ScriptedModelClient(ToolUseResponse(toolCall));

        var firstHarness = BuildHarness(firstClient, registry);
        var suspended = await firstHarness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, suspended.Status);

        var resumed = suspended.FinalState.ResumeWithHumanAnswer(callId, "Alice");
        var secondClient = new ScriptedModelClient(EndTurnResponse("Hello, Alice!"));
        var secondHarness = BuildHarness(secondClient);
        var final = await secondHarness.RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, final.Status);
        Assert.Equal("Hello, Alice!", final.FinalAnswer);
    }

    // ── AgentState.ResumeWithHumanAnswer ─────────────────────────────────────

    [Fact]
    public void ResumeWithHumanAnswer_ClearsPendingHumanInput()
    {
        var callId = Guid.NewGuid().ToString("n");
        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "Ready?", IsPending: true));

        var suspended = NewState()
            .AppendStep(pendingStep) with
            {
                Status = AgentStatus.AwaitingHuman,
                PendingHumanInput = new PendingHumanInput(callId, "Ready?")
            };

        var resumed = suspended.ResumeWithHumanAnswer(callId, "yes");

        Assert.Equal(AgentStatus.Running, resumed.Status);
        Assert.Null(resumed.PendingHumanInput);
    }

    [Fact]
    public void ResumeWithHumanAnswer_ReplacesPendingStep()
    {
        var callId = Guid.NewGuid().ToString("n");
        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "What is your name?", IsPending: true));

        var suspended = NewState()
            .AppendStep(pendingStep) with { Status = AgentStatus.AwaitingHuman };

        var resumed = suspended.ResumeWithHumanAnswer(callId, "Bob");

        Assert.Equal(AgentStatus.Running, resumed.Status);
        var resolvedStep = resumed.Trajectory.OfType<ToolCallStep>().Single();
        Assert.False(resolvedStep.Result.IsPending);
        Assert.Equal("Bob", resolvedStep.Result.Content);
    }

    [Fact]
    public void ResumeWithHumanAnswer_LeavesOtherStepsUntouched()
    {
        var callId = Guid.NewGuid().ToString("n");
        var otherCallId = Guid.NewGuid().ToString("n");

        var otherCall = new ToolCall(otherCallId, "calc", JsonDocument.Parse("{}").RootElement);
        var otherStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, otherCall,
            new ToolResult(otherCallId, "42"));

        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "Ready?", IsPending: true));

        var state = NewState().AppendStep(otherStep).AppendStep(pendingStep);
        var resumed = state.ResumeWithHumanAnswer(callId, "yes");

        var steps = resumed.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("42", steps[0].Result.Content);
        Assert.False(steps[0].Result.IsPending);
        Assert.Equal("yes", steps[1].Result.Content);
        Assert.False(steps[1].Result.IsPending);
    }
}
