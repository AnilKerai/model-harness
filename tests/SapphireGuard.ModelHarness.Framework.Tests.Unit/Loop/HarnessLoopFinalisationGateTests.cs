using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;
using BudgetNs = SapphireGuard.ModelHarness.Framework.Budget;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

/// <summary>
/// The forced-finalisation response is model output like any other, so it must pass the PostModelCall
/// content gates before it is handed back as <see cref="AgentOutcome.FinalAnswer"/>. The intervention-cap
/// path is the one that matters most: it runs exactly when a gate has just rejected three responses.
/// </summary>
public sealed class HarnessLoopFinalisationGateTests
{
    private const string SensorName = "pii-redaction";
    private const string SensorReason = "email detected";
    private const string BudgetReason = "test budget exhausted";
    private const string WithheldSentence = $" The finalisation response was withheld by sensor '{SensorName}'.";

    // ── Intervention cap ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InterventionCapReached_RejectingSensor_WithholdsTheFinalisationAnswer()
    {
        // The sensor rejects every response, so the cap (3) is hit and the fourth call is the finalisation.
        var client = new ScriptedModelClient(EndTurn("a@x.com"), EndTurn("b@x.com"), EndTurn("c@x.com"), EndTurn("d@x.com"));
        var harness = BuildHarness(client, [RejectingSensor()]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.Contains("intervention limit", outcome.FailureReason);
        Assert.EndsWith(WithheldSentence, outcome.FailureReason);
        Assert.Equal(4, client.CallCount); // cap + 1, and no retry after the withheld finalisation
    }

    [Fact]
    public async Task RunAsync_InterventionCapReached_RejectingSensor_RecordsTheFinalisationRejectionInTheTrajectory()
    {
        var client = new ScriptedModelClient(EndTurn("a"), EndTurn("b"), EndTurn("c"), EndTurn("d"));
        var harness = BuildHarness(client, [RejectingSensor()]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var interventions = outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().ToList();
        Assert.Equal(4, interventions.Count); // three that tripped the cap, one for the finalisation response
        Assert.All(interventions, i => Assert.Equal(HookPoint.PostModelCall, i.HookPoint));

        var finalisationCall = outcome.FinalState.Trajectory.OfType<ModelCallStep>().Last();
        Assert.Equal("d", finalisationCall.Response.Text);
        Assert.Same(finalisationCall, interventions[^1].TriggeringStep);
        Assert.Same(interventions[^1], outcome.FinalState.Trajectory[^1]);
    }

    [Fact]
    public async Task RunAsync_InterventionCapReached_SensorPassesTheFinalisationResponse_StillReturnsItAsPartialResult()
    {
        // Three rejections trip the cap; the fourth response is clean, so it is returned as before.
        var client = new ScriptedModelClient(EndTurn("a"), EndTurn("b"), EndTurn("c"), EndTurn("clean best effort"));
        var harness = BuildHarness(client, [new CountdownSensor(HookPoint.PostModelCall, blockCount: 3)]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Equal("clean best effort", outcome.FinalAnswer);
        Assert.DoesNotContain("withheld", outcome.FailureReason);
        Assert.Equal(4, client.CallCount);
    }

    // ── Budget exhaustion ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_BudgetExhausted_RejectingSensor_WithholdsTheFinalisationAnswer()
    {
        var client = new ScriptedModelClient(EndTurn("a@x.com"));
        var harness = BuildHarness(client, [RejectingSensor()], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.Equal(BudgetReason + WithheldSentence, outcome.FailureReason);
        Assert.Equal(1, client.CallCount); // no retry after the withheld finalisation
        var intervention = Assert.Single(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>());
        Assert.Equal(HookPoint.PostModelCall, intervention.HookPoint);
        Assert.Equal(SensorName, intervention.SensorName);
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_PassingSensor_LeavesTheFinalAnswerAsBefore()
    {
        var client = new ScriptedModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, [new AlwaysPassSensor(HookPoint.PostModelCall)], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Equal("best I can do", outcome.FinalAnswer);
        Assert.Equal(BudgetReason, outcome.FailureReason);
        Assert.Empty(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>());
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_TruncatedAndRejected_ReasonNamesBudgetTruncationAndSensor()
    {
        var truncated = new ModelResponse
        {
            Text = "a@x.com and then the",
            ToolCalls = [],
            StopReason = StopReason.MaxTokens,
            Usage = Usage.Zero,
            Cost = 0m
        };
        var client = new ScriptedModelClient(truncated);
        var harness = BuildHarness(client, [RejectingSensor()], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.StartsWith(BudgetReason, outcome.FailureReason);
        Assert.Contains("also cut short", outcome.FailureReason);
        Assert.EndsWith(WithheldSentence, outcome.FailureReason);
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_SeveralSensorsReject_ReasonNamesTheFirstAndTrajectoryRecordsAll()
    {
        var client = new ScriptedModelClient(EndTurn("a@x.com"));
        var harness = BuildHarness(client,
            [
                new CountdownSensor(HookPoint.PostModelCall, blockCount: 1, name: "first", reason: "r1"),
                new CountdownSensor(HookPoint.PostModelCall, blockCount: 1, name: "second", reason: "r2")
            ],
            new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Null(outcome.FinalAnswer);
        Assert.Equal($"{BudgetReason} The finalisation response was withheld by sensor 'first'.", outcome.FailureReason);
        Assert.Equal(["first", "second"], outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Select(s => s.SensorName));
    }

    [Theory]
    [InlineData(true)]  // intervention-cap path
    [InlineData(false)] // budget-exhaustion path
    public async Task RunAsync_FinalisationWithheldByPiiSensor_FailureReasonDoesNotQuoteTheMatchedPii(bool viaInterventionCap)
    {
        // PiiRedactionSensor's reason quotes the matched value, and FailureReason reaches the caller, a
        // parent agent's tool result and the tracers. The outcome must name the sensor but not carry the PII.
        const string email = "john.doe@example.com";
        var client = new ScriptedModelClient(
            EndTurn($"mail {email}"), EndTurn($"mail {email}"), EndTurn($"mail {email}"), EndTurn($"mail {email}"));
        var harness = viaInterventionCap
            ? BuildHarness(client, [new PiiRedactionSensor()])
            : BuildHarness(client, [new PiiRedactionSensor()], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.EndsWith("withheld by sensor 'pii-redaction'.", outcome.FailureReason);
        Assert.DoesNotContain(email, outcome.FailureReason);
        Assert.DoesNotContain("john.doe", outcome.FailureReason);
        // The full reason is still recorded for audit, on the intervention step the tracers gate.
        Assert.Contains(email, outcome.FinalState.Trajectory.OfType<SensorInterventionStep>().Last().Reason);
    }

    // ── Spend and scope ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)] // sensor passes the finalisation response
    [InlineData(1)] // sensor rejects it
    public async Task RunAsync_BudgetExhausted_FinalisationSensorSpendIsAccruedOnState(int blockCount)
    {
        var sensor = new UsageReportingSensor(HookPoint.PostModelCall, new Usage(80, 20), cost: 0.10m, blockCount: blockCount);
        var client = new ScriptedModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, [sensor], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Equal(80, outcome.FinalState.SensorUsage.InputTokens);
        Assert.Equal(20, outcome.FinalState.SensorUsage.OutputTokens);
        Assert.Equal(0.10m, outcome.FinalState.SensorCost);
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_PreReturnSensorIsNotConsultedOnTheFinalisationResponse()
    {
        // PartialResult already tells the caller the answer is not final, so PreReturn does not run. The
        // sensor would block 99 times; had it been consulted there would be an intervention in the trajectory.
        var client = new ScriptedModelClient(EndTurn("best I can do"));
        var harness = BuildHarness(client, [new CountdownSensor(HookPoint.PreReturn, blockCount: 99)], new AlwaysExhaustedBudgetEnforcer());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Equal("best I can do", outcome.FinalAnswer);
        Assert.Empty(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CountdownSensor RejectingSensor() =>
        new(HookPoint.PostModelCall, blockCount: int.MaxValue, name: SensorName, reason: SensorReason);

    private static HarnessLoop BuildHarness(
        IModelClient modelClient,
        IEnumerable<ISensor> sensors,
        BudgetNs.IBudgetEnforcer? budgetEnforcer = null) =>
        new(modelClient: modelClient,
            toolRegistry: new StubToolRegistry(),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner(sensors),
            budgetEnforcer: budgetEnforcer ?? new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

    private static AgentState NewState() =>
        AgentState.NewTask("test task", new StateBudget
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
