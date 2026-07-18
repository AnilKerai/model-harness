using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Budget;

public sealed class DefaultBudgetEnforcerTests
{
    private static readonly StateBudget Generous = new()
    {
        MaxTurns = 10,
        MaxTotalTokens = 100_000,
        MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    };

    private static AgentState EmptyState(StateBudget? budget = null) =>
        AgentState.NewTask("test", budget ?? Generous, DateTimeOffset.UtcNow);

    // A state whose first user message (the wall-clock anchor) is at a chosen instant.
    private static AgentState StateStartedAt(DateTimeOffset firstMessageAt, StateBudget? budget = null) =>
        AgentState.NewTask("test", budget ?? Generous, firstMessageAt);

    private static ModelCallStep ModelStep(decimal cost = 0m, int inputTokens = 0, int outputTokens = 0) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, [], new ModelResponse
        {
            Text = null, ToolCalls = [], StopReason = StopReason.EndTurn,
            Usage = new Usage(inputTokens, outputTokens), Cost = cost
        }, new Usage(inputTokens, outputTokens), cost);

    private static readonly DefaultBudgetEnforcer Sut = new();

    [Fact]
    public void Check_UnderAllLimits_ReturnsOk()
    {
        var state = EmptyState();
        var result = Sut.Check(state);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_TurnsAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxTurns = 2 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep())
            .AppendStep(ModelStep());

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxTurns", result.Reason);
    }

    [Fact]
    public void Check_CostAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxCost = 1m };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(cost: 0.5m))
            .AppendStep(ModelStep(cost: 0.5m));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_TokensAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxTotalTokens = 100 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(inputTokens: 60, outputTokens: 40));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxTotalTokens", result.Reason);
    }

    [Fact]
    public void Check_WallClockExceeded_ReturnsExhausted()
    {
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };
        // First user message ten seconds ago — well past the 1ms wall-clock limit.
        var state = StateStartedAt(DateTimeOffset.UtcNow.AddSeconds(-10), budget);

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxWallClock", result.Reason);
    }

    [Fact]
    public void Check_WallClock_MeasuredFromFirstUserMessage_NotFromLatestTurn()
    {
        // The resume/multi-turn property: even with a later user turn, the task-mode enforcer measures
        // wall-clock from the FIRST user message (the true task start), so it stays exhausted rather
        // than resetting. This is what a per-invocation `startedAt` used to break across a resume.
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };
        var state = StateStartedAt(DateTimeOffset.UtcNow.AddSeconds(-10), budget)
            .AppendStep(ModelStep())
            .WithUserMessage("a later turn", DateTimeOffset.UtcNow); // recent, but must not reset the clock

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxWallClock", result.Reason);
    }

    [Fact]
    public void Check_WallClock_UsesInjectedTimeProvider()
    {
        // The clock reads the injected TimeProvider, and the anchor is the first message's timestamp:
        // both are `frozen`, so elapsed is zero — yet frozen is years in the real past, so an enforcer
        // reading the system clock would report it long exhausted.
        var frozen = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = new DefaultBudgetEnforcer(new FixedClock(frozen));
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };

        var result = sut.Check(StateStartedAt(frozen, budget));

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_Lookahead_CountsTowardWallClock()
    {
        // The rate-limit guard passes the backoff as a lookahead: elapsed alone is under the limit,
        // but elapsed + lookahead exceeds it, so the enforcer reports exhausted before the loop sleeps.
        var budget = Generous with { MaxWallClock = TimeSpan.FromSeconds(2) };
        var state = StateStartedAt(DateTimeOffset.UtcNow.AddSeconds(-1), budget); // ~1s elapsed

        Assert.False(Sut.Check(state).IsExhausted);                          // 1s < 2s
        Assert.True(Sut.Check(state, lookahead: TimeSpan.FromSeconds(2)).IsExhausted); // 1s + 2s >= 2s
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Check_NonModelCallStepsIgnored_DoesNotInflateTurnCount()
    {
        var budget = Generous with { MaxTurns = 1 };
        // Trajectory has tool steps but no model call steps — turns should be 0
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "t", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok"));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_ToolCallStepWithDelegatedCost_ExhaustsWhenTotalCostReachesLimit()
    {
        var budget = Generous with { MaxCost = 1m };
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok", Cost: 1.0m));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_ToolCallStepWithDelegatedUsage_ExhaustsWhenTokensReachLimit()
    {
        var budget = Generous with { MaxTotalTokens = 100 };
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok", Usage: new Usage(60, 40)));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxTotalTokens", result.Reason);
    }

    [Fact]
    public void Check_ToolCallStepWithNullCostAndUsage_DoesNotContributeToTotals()
    {
        var budget = Generous with { MaxCost = 0.5m, MaxTotalTokens = 50 };
        // Tool result with null Cost and null Usage should not push over the limit
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok"));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_ModelCallAndToolCallCostsCombined_AggregatesCorrectly()
    {
        var budget = Generous with { MaxCost = 1m };
        // 0.6 from model call + 0.5 from delegated tool = 1.1 ≥ 1.0
        var state = EmptyState(budget)
            .AppendStep(ModelStep(cost: 0.6m))
            .AppendStep(new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
                new Tools.ToolResult("id", "ok", Cost: 0.5m)));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_SensorCostAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxCost = 1m };
        var state = EmptyState(budget) with { SensorCost = 1m };

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_SensorUsageAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxTotalTokens = 100 };
        var state = EmptyState(budget) with { SensorUsage = new Usage(60, 40) };

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxTotalTokens", result.Reason);
    }

    [Fact]
    public void Check_SensorModelAndToolCostsCombined_AggregatesCorrectly()
    {
        var budget = Generous with { MaxCost = 1m };
        // 0.3 from sensor + 0.4 from model call + 0.4 from delegated tool = 1.1 ≥ 1.0
        var state = (EmptyState(budget) with { SensorCost = 0.3m })
            .AppendStep(ModelStep(cost: 0.4m))
            .AppendStep(new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
                new Tools.ToolResult("id", "ok", Cost: 0.4m)));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_CompactionCostAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxCost = 1m };
        var state = EmptyState(budget) with { CompactionCost = 1m };

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_CompactionUsageAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxTotalTokens = 100 };
        var state = EmptyState(budget) with { CompactionUsage = new Usage(60, 40) };

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxTotalTokens", result.Reason);
    }
}
