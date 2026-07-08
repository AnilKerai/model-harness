using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
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
        IToolRegistry? toolRegistry = null,
        BudgetNs.IBudgetEnforcer? budgetEnforcer = null,
        Tracing.ITracer? tracer = null)
    {
        var sensorRunner = new DefaultSensorRunner(sensors ?? []);
        return new HarnessLoop(
            modelClient: modelClient,
            toolRegistry: toolRegistry ?? new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: sensorRunner,
            budgetEnforcer: budgetEnforcer ?? new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: tracer ?? new NullTracer(),
            checkpointStore: new NullCheckpointStore());
    }

    private static AgentState NewState(string task = "test task") =>
        AgentState.NewTask(task, new SapphireGuard.ModelHarness.Framework.State.Budget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

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

    // ── Tool-call deadline ────────────────────────────────────────────────────

    [Fact(Timeout = 5000)]
    public async Task RunAsync_ToolExceedsDeadline_ReturnsRecoverableErrorAndContinues()
    {
        // A tool that blocks on its token is cancelled by the per-tool deadline, surfaced as an
        // IsError result; the run continues so the model can replan (it does not fail the run).
        var registry = new BlockingToolRegistry();
        var client = new ScriptedModelClient(
            ToolUseResponse(MakeToolCall("slow-tool")),
            EndTurnResponse("recovered after timeout"));
        var harness = BuildHarness(client, toolRegistry: registry);

        var state = AgentState.NewTask("test task", new SapphireGuard.ModelHarness.Framework.State.Budget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1),
            MaxToolCallDuration = TimeSpan.FromMilliseconds(50)
        }, DateTimeOffset.UtcNow);

        var outcome = await harness.RunAsync(state, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("recovered after timeout", outcome.FinalAnswer);
        Assert.Equal(1, registry.DispatchCount);

        var toolStep = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.True(toolStep.Result.IsError);
        Assert.Contains("deadline", toolStep.Result.Content);
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


    // ── Sensor usage propagation ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PassingSensorWithUsage_AccumulatesSensorCostAndUsageOnState()
    {
        var sensor = new UsageReportingSensor(HookPoint.PreModelCall, new Usage(100, 50), cost: 0.25m);
        var client = new ScriptedModelClient(EndTurnResponse("ok"));

        var harness = BuildHarness(client, sensors: [sensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(100, outcome.FinalState.SensorUsage.InputTokens);
        Assert.Equal(50, outcome.FinalState.SensorUsage.OutputTokens);
        Assert.Equal(0.25m, outcome.FinalState.SensorCost);
    }

    [Fact]
    public async Task RunAsync_InterveningSensorWithUsage_AccumulatesSensorCostAndUsageOnState()
    {
        // Sensor intervenes once then passes — reports usage on both fires.
        var sensor = new UsageReportingSensor(HookPoint.PreReturn, new Usage(80, 20), cost: 0.10m, blockCount: 1);
        var client = new ScriptedModelClient(
            EndTurnResponse("attempt 1"),  // PreReturn blocks this
            EndTurnResponse("attempt 2")); // PreReturn passes

        var harness = BuildHarness(client, sensors: [sensor]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        // Sensor fires twice (once per model call), so usage doubles
        Assert.Equal(160, outcome.FinalState.SensorUsage.InputTokens);
        Assert.Equal(40, outcome.FinalState.SensorUsage.OutputTokens);
        Assert.Equal(0.20m, outcome.FinalState.SensorCost);
    }

    [Fact]
    public async Task RunAsync_MultipleSensorsWithUsage_TotalsAreSummedCorrectly()
    {
        var s1 = new UsageReportingSensor(HookPoint.PreModelCall, new Usage(50, 10), cost: 0.05m, name: "s1");
        var s2 = new UsageReportingSensor(HookPoint.PreModelCall, new Usage(30, 20), cost: 0.03m, name: "s2");
        var client = new ScriptedModelClient(EndTurnResponse("ok"));

        var harness = BuildHarness(client, sensors: [s1, s2]);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(80, outcome.FinalState.SensorUsage.InputTokens);
        Assert.Equal(30, outcome.FinalState.SensorUsage.OutputTokens);
        Assert.Equal(0.08m, outcome.FinalState.SensorCost);
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

    [Fact]
    public async Task RunAsync_SensorThrows_FailsOpenAndRunCompletes()
    {
        // A throwing sensor must not take the run down: the runner surfaces it as a non-intervention
        // error result, so the model keeps its turn and the run completes normally.
        var sensor = new ThrowingSensor(HookPoint.PreModelCall);
        var client = new ScriptedModelClient(EndTurnResponse("ok"));
        var harness = BuildHarness(client, sensors: [sensor]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("ok", outcome.FinalAnswer);
        Assert.Empty(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>());
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
        Assert.Equal(4, trajectory.Count);
        Assert.IsType<UserMessageStep>(trajectory[0]); // seeded task
        Assert.IsType<ModelCallStep>(trajectory[1]);   // model → ToolUse
        Assert.IsType<ToolCallStep>(trajectory[2]);    // tool dispatch
        Assert.IsType<ModelCallStep>(trajectory[3]);   // model → EndTurn
    }

    [Fact]
    public async Task RunAsync_ContinuedWithUserMessage_AccumulatesUserTurnsAcrossRuns()
    {
        // A Done run is re-opened by appending a user message; the second run continues
        // the same trajectory rather than starting fresh.
        var client = new ScriptedModelClient(
            EndTurnResponse("first answer"),
            EndTurnResponse("second answer"));
        var harness = BuildHarness(client);

        var first = await harness.RunAsync(NewState(), CancellationToken.None);
        Assert.Equal(AgentStatus.Done, first.Status);

        var second = await harness.RunAsync(
            first.FinalState.WithUserMessage("a follow-up", DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, second.Status);
        Assert.Equal("second answer", second.FinalAnswer);

        var userTurns = second.FinalState.Trajectory.OfType<UserMessageStep>().Select(s => s.Content).ToList();
        Assert.Equal(new[] { "test task", "a follow-up" }, userTurns);
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

    // ── Tracer scopes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ModelCall_CompletesModelScopeWithResponseThenDisposes()
    {
        var tracer = new RecordingTracer();
        var client = new ScriptedModelClient(EndTurnResponse("hello"));
        var harness = BuildHarness(client, tracer: tracer);

        await harness.RunAsync(NewState(), CancellationToken.None);

        var completed = Assert.Single(tracer.ModelCompletions);
        Assert.Equal("hello", completed.Text);
        Assert.Equal(1, tracer.ModelScopesDisposed);
    }

    [Fact]
    public async Task RunAsync_ModelCallThrows_DisposesModelScopeWithoutCompleting()
    {
        // When the call throws, the loop must NOT call Complete, must record the exception via
        // Fail (so the span carries the detail), and must still dispose the scope.
        var tracer = new RecordingTracer();
        var harness = BuildHarness(new ScriptedModelClient(), tracer: tracer); // no responses → throws on first call

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Failed, outcome.Status);
        Assert.Empty(tracer.ModelCompletions);
        Assert.Equal(1, tracer.ModelScopesDisposed);
        Assert.IsType<InvalidOperationException>(Assert.Single(tracer.ModelFailures));
    }

    [Fact]
    public async Task RunAsync_ToolCall_CompletesToolScopeWithResult()
    {
        var tracer = new RecordingTracer();
        var client = new ScriptedModelClient(
            ToolUseResponse(MakeToolCall("calc")),
            EndTurnResponse("done"));
        var harness = BuildHarness(client, tracer: tracer);

        await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Single(tracer.ToolCompletions);
    }

    [Fact]
    public async Task RunAsync_EachTurn_LogsCheckpointAndBudgetSnapshot()
    {
        // Every turn opens with a checkpoint save and a budget snapshot — both must reach the tracer.
        var tracer = new RecordingTracer();
        var client = new ScriptedModelClient(ToolUseResponse(MakeToolCall("t")), EndTurnResponse("done"));
        var harness = BuildHarness(client, tracer: tracer);

        await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(new[] { 0, 1 }, tracer.CheckpointTurns);       // one per turn (ToolUse turn, then EndTurn turn)
        Assert.Equal(new[] { 0, 1 }, tracer.BudgetSnapshotTurns);
        Assert.Equal(10, tracer.BudgetSnapshots[0].MaxTurns);
        Assert.Equal(0, tracer.BudgetSnapshots[0].TurnsUsed);       // snapshot taken at the top of the turn, before that turn's model call
        Assert.Equal(1, tracer.BudgetSnapshots[1].TurnsUsed);       // turn 0's model call is now counted
    }

    [Fact]
    public async Task RunAsync_RateLimited_LogsTheWaitThenProceeds()
    {
        var tracer = new RecordingTracer();
        var harness = new HarnessLoop(
            modelClient: new ScriptedModelClient(EndTurnResponse("done")),
            toolRegistry: new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new OnceLimitingRateLimiter(TimeSpan.FromMilliseconds(1)),
            tracer: tracer,
            checkpointStore: new NullCheckpointStore());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(tracer.RateLimitDelays));
    }

    // ── Compaction persistence ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ContextBuildReportsCompaction_PersistsSummaryAndCountsSpend()
    {
        // The write-back: when the trajectory guide folds (surfaced on ContextBuildResult), the loop
        // commits the rolling summary onto the next state and accumulates the strategy's spend.
        var compaction = new CompactionResult
        {
            InjectedText = "rolling",
            UpdatedSummary = new RollingSummary("rolling", 3),
            Usage = new Usage(100, 20),
            Cost = 0.5m
        };
        var harness = new HarnessLoop(
            modelClient: new ScriptedModelClient(EndTurnResponse("done")),
            toolRegistry: new StubToolRegistry(),
            contextBuilder: new CompactingContextBuilder(compaction),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal("rolling", outcome.FinalState.RollingSummary?.Text);
        Assert.Equal(3, outcome.FinalState.RollingSummary?.FoldedStepCount);
        Assert.Equal(0.5m, outcome.FinalState.CompactionCost);
        Assert.Equal(120, outcome.FinalState.CompactionUsage.TotalTokens);
    }

    // ── Pinned reference content ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolReturnsPins_CommitsThemToState()
    {
        var client = new ScriptedModelClient(ToolUseResponse(MakeToolCall("load")), EndTurnResponse("done"));
        var registry = new StubToolRegistry(c => new ToolResult(c.CallId, "ack", Pins: [new PinnedNote("Skill: x", "the body")]));
        var harness = BuildHarness(client, toolRegistry: registry);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var pin = Assert.Single(outcome.FinalState.Pins);
        Assert.Equal("Skill: x", pin.Label);
        Assert.Equal("the body", pin.Content);
    }

    [Fact]
    public async Task RunAsync_ToolRepinsSameLabel_ReplacesNotDuplicates()
    {
        var client = new ScriptedModelClient(
            ToolUseResponse(MakeToolCall("a")), ToolUseResponse(MakeToolCall("b")), EndTurnResponse("done"));
        var n = 0;
        var registry = new StubToolRegistry(c => new ToolResult(c.CallId, "ack", Pins: [new PinnedNote("Skill: x", $"body {++n}")]));
        var harness = BuildHarness(client, toolRegistry: registry);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var pin = Assert.Single(outcome.FinalState.Pins);   // replaced by label, not duplicated
        Assert.Equal("body 2", pin.Content);
    }
}

// ── Extra test double ─────────────────────────────────────────────────────────

file sealed class ThrowingModelClient(string message) : Model.IModelClient
{
    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        throw new InvalidOperationException(message);
}

// A tool that blocks until its cancellation token fires — used to exercise the per-tool deadline.
file sealed class BlockingToolRegistry : IToolRegistry
{
    private int _dispatchCount;
    public int DispatchCount => _dispatchCount;

    public IReadOnlyList<ITool> List() => [];
    public ITool? Get(string name) => null;

    public async Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _dispatchCount);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return new ToolResult(call.CallId, "unreachable");
    }
}

// Reports a fixed compaction result on every build, as the trajectory guide would after a fold.
file sealed class CompactingContextBuilder(CompactionResult compaction) : IContextBuilder
{
    public Task<ContextBuildResult> BuildAsync(AgentState state, IReadOnlyList<ITool> tools, CancellationToken ct) =>
        Task.FromResult(new ContextBuildResult(
            [new Message(MessageRole.User, state.TaskText)], tools, compaction));
}

// Records how the loop drives the model/tool trace scopes.
file sealed class RecordingTracer : Tracing.ITracer
{
    public List<ModelResponse> ModelCompletions { get; } = [];
    public int ModelScopesDisposed { get; private set; }
    public List<Exception> ModelFailures { get; } = [];
    public List<ToolResult> ToolCompletions { get; } = [];
    public List<int> CheckpointTurns { get; } = [];
    public List<int> BudgetSnapshotTurns { get; } = [];
    public List<BudgetSnapshot> BudgetSnapshots { get; } = [];
    public List<TimeSpan> RateLimitDelays { get; } = [];

    public void StartTrace(string taskId, string taskText) { }
    public Tracing.IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools) => new ModelScope(this);
    public Tracing.IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) => new ToolScope(this);
    public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) { }
    public void Complete(string taskId, AgentStatus status, string? failureReason) { }
    public void LogCheckpoint(string taskId, int turn, string checkpointId, TimeSpan elapsed) => CheckpointTurns.Add(turn);
    public void LogBudgetSnapshot(string taskId, int turn, BudgetSnapshot snapshot) { BudgetSnapshotTurns.Add(turn); BudgetSnapshots.Add(snapshot); }
    public void LogRateLimit(string taskId, int turn, TimeSpan delay) => RateLimitDelays.Add(delay);

    private sealed class ModelScope(RecordingTracer owner) : Tracing.IModelCallScope
    {
        public void Complete(ModelResponse response) => owner.ModelCompletions.Add(response);
        public void Fail(Exception exception) => owner.ModelFailures.Add(exception);
        public void Dispose() => owner.ModelScopesDisposed++;
    }

    private sealed class ToolScope(RecordingTracer owner) : Tracing.IToolCallScope
    {
        public void Complete(ToolResult result) => owner.ToolCompletions.Add(result);
        public void Dispose() { }
    }
}

// Limits exactly once (then passes) so the loop takes its rate-limit backoff path a single time.
file sealed class OnceLimitingRateLimiter(TimeSpan delay) : IRateLimiter
{
    private bool _limited;
    public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct)
    {
        if (_limited) return Task.FromResult(RateLimitCheck.Pass);
        _limited = true;
        return Task.FromResult(RateLimitCheck.Limited(delay, "test throttle"));
    }
}

