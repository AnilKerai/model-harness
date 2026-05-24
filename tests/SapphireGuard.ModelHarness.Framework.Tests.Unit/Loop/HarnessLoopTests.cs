using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Loop;
using BudgetNs = SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

public sealed class HarnessLoopTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HarnessLoop BuildHarness(
        ScriptedModelClient modelClient,
        IEnumerable<ISensor>? sensors = null,
        StubToolRegistry? toolRegistry = null,
        BudgetNs.IBudgetEnforcer? budgetEnforcer = null)
    {
        var sensorRunner = new DefaultSensorRunner(sensors ?? []);
        return new HarnessLoop(
            modelClient: modelClient,
            toolRegistry: toolRegistry ?? new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: sensorRunner,
            budgetEnforcer: budgetEnforcer ?? new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());
    }

    private static AgentState NewState(string task = "test task") =>
        AgentState.NewTask(task, new SapphireGuard.ModelHarness.Framework.State.Budget
        {
            MaxTurns = 10,
            MaxContextTokens = 100_000,
            MaxCostUsd = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });

    private static ModelResponse EndTurnResponse(string text = "final answer") => new()
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

    private static ToolCall MakeToolCall(string name = "test-tool") =>
        new(Guid.NewGuid().ToString("n"), name, JsonDocument.Parse("{}").RootElement);

    // ── Basic outcomes ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ModelReturnsEndTurnImmediately_ReturnsDoneWithAnswer()
    {
        var client = new ScriptedModelClient(EndTurnResponse("the answer"));
        var harness = BuildHarness(client);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("the answer", outcome.FinalAnswer);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task RunAsync_ModelUsesTool_DispatchesToolThenContinues()
    {
        var toolCall = MakeToolCall("calc");
        var toolRegistry = new StubToolRegistry(_ => new ToolResult(toolCall.CallId, "42"));
        var client = new ScriptedModelClient(
            ToolUseResponse(toolCall),
            EndTurnResponse("the answer is 42"));

        var harness = BuildHarness(client, toolRegistry: toolRegistry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("the answer is 42", outcome.FinalAnswer);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, toolRegistry.DispatchCount);

        var toolStep = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.Equal("calc", toolStep.Call.ToolName);
        Assert.Equal("42", toolStep.Result.Content);
        Assert.False(toolStep.Result.IsError);
    }

    [Fact]
    public async Task RunAsync_ModelRequestsMultipleTools_AllAreDispatched()
    {
        var call1 = MakeToolCall("tool-a");
        var call2 = MakeToolCall("tool-b");
        var toolRegistry = new StubToolRegistry();
        var client = new ScriptedModelClient(
            ToolUseResponse(call1, call2),
            EndTurnResponse("done"));

        var harness = BuildHarness(client, toolRegistry: toolRegistry);
        await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(2, toolRegistry.DispatchCount);
    }

    // ── Budget ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_BudgetExhaustedImmediately_ReturnsPartialResultWithoutCallingModel()
    {
        // The forced-finalise call still happens — that's the contract
        var client = new ScriptedModelClient(EndTurnResponse("best I can do"));
        var harness = BuildHarness(client, budgetEnforcer: new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Equal("test budget exhausted", outcome.FailureReason);
        Assert.Equal(1, client.CallCount); // one forced-finalise call
    }

    // ── PreModelCall sensor ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PreModelCallIntervened_InjectsNoteAndProceedsWithModelCall()
    {
        // PreModelCall sensors inject a guidance note into the trajectory, then the
        // model call proceeds on the same turn so the model can act on the note.
        var sensor = new CountdownSensor(HookPoint.PreModelCall, blockCount: 1, reason: "not now");
        var client = new ScriptedModelClient(EndTurnResponse("ok"));

        var harness = BuildHarness(client, sensors: [sensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(1, client.CallCount); // model called on the same turn the sensor fired

        var intervention = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Single();
        Assert.Equal(HookPoint.PreModelCall, intervention.HookPoint);
        Assert.Equal("not now", intervention.Reason);
    }

    // ── PostModelCall sensor ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PostModelCallIntervened_OnEndTurn_IntervenedResponseNotReturnedAsFinalAnswer()
    {
        // The critical PII scenario: first response contains sensitive content
        // and is blocked. The model is given another chance and responds cleanly.
        var sensor = new CountdownSensor(HookPoint.PostModelCall, blockCount: 1, reason: "pii detected");
        var client = new ScriptedModelClient(
            EndTurnResponse("sensitive content john@example.com"),
            EndTurnResponse("clean answer"));

        var harness = BuildHarness(client, sensors: [sensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("clean answer", outcome.FinalAnswer);
        Assert.DoesNotContain("john@example.com", outcome.FinalAnswer ?? "");
        Assert.Equal(2, client.CallCount);

        var intervention = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Single();
        Assert.Equal(HookPoint.PostModelCall, intervention.HookPoint);
    }

    [Fact]
    public async Task RunAsync_PostModelCallIntervened_OnToolUse_ToolCallsNotDispatched()
    {
        // Sensor blocks a ToolUse response — tool must not execute.
        var toolCall = MakeToolCall("dangerous-tool");
        var toolRegistry = new StubToolRegistry();
        var sensor = new CountdownSensor(HookPoint.PostModelCall, blockCount: 1);
        var client = new ScriptedModelClient(
            ToolUseResponse(toolCall),  // blocked — tool must not run
            EndTurnResponse("done"));

        var harness = BuildHarness(client, sensors: [sensor], toolRegistry: toolRegistry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(0, toolRegistry.DispatchCount);
        Assert.Equal(2, client.CallCount);
    }

    // ── PreReturn sensor ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PreReturnIntervened_LoopsAndAllowsModelToReplan()
    {
        var sensor = new CountdownSensor(HookPoint.PreReturn, blockCount: 1, reason: "not ready");
        var client = new ScriptedModelClient(
            EndTurnResponse("first attempt"),  // PreReturn blocks this
            EndTurnResponse("revised answer")); // passes

        var harness = BuildHarness(client, sensors: [sensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("revised answer", outcome.FinalAnswer);
        Assert.Equal(2, client.CallCount);

        var intervention = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Single();
        Assert.Equal(HookPoint.PreReturn, intervention.HookPoint);
    }

    // ── PreToolCall sensor ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PreToolCallIntervened_ToolNotDispatchedAndErrorResultCreated()
    {
        var toolCall = MakeToolCall("blocked-tool");
        var toolRegistry = new StubToolRegistry();
        var sensor = new CountdownSensor(HookPoint.PreToolCall, blockCount: 1, reason: "tool not permitted");
        var client = new ScriptedModelClient(
            ToolUseResponse(toolCall),
            EndTurnResponse("ok, using something else"));

        var harness = BuildHarness(client, sensors: [sensor], toolRegistry: toolRegistry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(0, toolRegistry.DispatchCount);

        var toolStep = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.True(toolStep.Result.IsError);
        Assert.Contains("tool not permitted", toolStep.Result.Content);

        var intervention = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Single();
        Assert.Equal(HookPoint.PreToolCall, intervention.HookPoint);
    }

    // ── PostToolCall sensor ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PostToolCallIntervened_InterventionRecordedAndModelReseesContext()
    {
        // PostToolCall block is advisory — the tool result is already in the
        // trajectory and the model will see both the result and the intervention.
        var toolCall = MakeToolCall("data-tool");
        var toolRegistry = new StubToolRegistry(_ => new ToolResult(toolCall.CallId, "-999"));
        var sensor = new CountdownSensor(HookPoint.PostToolCall, blockCount: 1, reason: "result out of range");
        var client = new ScriptedModelClient(
            ToolUseResponse(toolCall),
            EndTurnResponse("adjusted answer"));

        var harness = BuildHarness(client, sensors: [sensor], toolRegistry: toolRegistry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(1, toolRegistry.DispatchCount); // tool DID run
        Assert.Equal(2, client.CallCount);

        Assert.Single(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>());
        Assert.Single(outcome.FinalState.Trajectory.OfType<ToolCallStep>());
    }


    // ── Cancellation and exceptions ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CancelledBeforeFirstTurn_ReturnsFailed()
    {
        var client = new ScriptedModelClient(EndTurnResponse());
        var harness = BuildHarness(client);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await harness.RunAsync(NewState(), cts.Token);

        Assert.Equal(AgentStatus.Failed, outcome.Status);
        Assert.Equal("cancelled", outcome.FailureReason);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task RunAsync_ModelClientThrows_ReturnsFailedWithReason()
    {
        var client = new ThrowingModelClient("API unavailable");
        var sensorRunner = new DefaultSensorRunner([]);
        var harness = new HarnessLoop(
            modelClient: client,
            toolRegistry: new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: sensorRunner,
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, outcome.Status);
        Assert.Contains("API unavailable", outcome.FailureReason);
    }

    // ── Trajectory integrity ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_HappyPathWithTool_TrajectoryContainsCorrectStepsInOrder()
    {
        var toolCall = MakeToolCall("calc");
        var client = new ScriptedModelClient(
            ToolUseResponse(toolCall),
            EndTurnResponse("done"));

        var harness = BuildHarness(client);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var trajectory = outcome.FinalState.Trajectory;
        Assert.Equal(3, trajectory.Count);
        Assert.IsType<ModelCallStep>(trajectory[0]); // model → ToolUse
        Assert.IsType<ToolCallStep>(trajectory[1]);  // tool dispatch
        Assert.IsType<ModelCallStep>(trajectory[2]); // model → EndTurn
    }

    [Fact]
    public async Task RunAsync_MultipleBlocksAcrossHookpoints_AllInterventionsRecorded()
    {
        // PreModelCall blocks force-finalise immediately (no loop), so we test two
        // hookpoints that both loop: PostModelCall and PreReturn.
        var postModelSensor = new CountdownSensor(HookPoint.PostModelCall, blockCount: 1, name: "post-model");
        var preReturnSensor = new CountdownSensor(HookPoint.PreReturn, blockCount: 1, name: "pre-return");

        var client = new ScriptedModelClient(
            EndTurnResponse("attempt 1"), // PostModelCall blocks this
            EndTurnResponse("attempt 2"), // PreReturn blocks this
            EndTurnResponse("final"));    // passes

        var harness = BuildHarness(client, sensors: [postModelSensor, preReturnSensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        var interventions = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().ToList();
        Assert.Equal(2, interventions.Count);
        Assert.Contains(interventions, s => s.SensorName == "post-model");
        Assert.Contains(interventions, s => s.SensorName == "pre-return");
    }
}

// ── Extra test double ─────────────────────────────────────────────────────────

file sealed class ThrowingModelClient(string message) : SapphireGuard.ModelHarness.Framework.Model.IModelClient
{
    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        throw new InvalidOperationException(message);
}

