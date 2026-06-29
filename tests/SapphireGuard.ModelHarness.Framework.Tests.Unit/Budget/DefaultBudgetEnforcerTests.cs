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
        MaxContextTokens = 100_000,
        MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    };

    private static AgentState EmptyState(StateBudget? budget = null) =>
        AgentState.NewTask("test", budget ?? Generous, DateTimeOffset.UtcNow);

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
        var result = Sut.Check(state, DateTimeOffset.UtcNow);
        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_TurnsAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxTurns = 2 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep())
            .AppendStep(ModelStep());

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_TokensAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxContextTokens = 100 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(inputTokens: 60, outputTokens: 40));

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxContextTokens", result.Reason);
    }

    [Fact]
    public void Check_WallClockExceeded_ReturnsExhausted()
    {
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-10);

        var result = Sut.Check(EmptyState(budget), startedAt);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxWallClock", result.Reason);
    }

    [Fact]
    public void Check_WallClock_UsesInjectedTimeProvider()
    {
        // startedAt == the injected clock's "now", so elapsed is zero — yet it is years in the
        // real past, so an enforcer reading the system clock would report it long exhausted.
        var frozen = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = new DefaultBudgetEnforcer(new FixedClock(frozen));
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };

        var result = sut.Check(EmptyState(budget), frozen);

        Assert.False(result.IsExhausted);
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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_ToolCallStepWithDelegatedUsage_ExhaustsWhenTokensReachLimit()
    {
        var budget = Generous with { MaxContextTokens = 100 };
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok", Usage: new Usage(60, 40)));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxContextTokens", result.Reason);
    }

    [Fact]
    public void Check_ToolCallStepWithNullCostAndUsage_DoesNotContributeToTotals()
    {
        var budget = Generous with { MaxCost = 0.5m, MaxContextTokens = 50 };
        // Tool result with null Cost and null Usage should not push over the limit
        var toolStep = new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "ok"));
        var state = EmptyState(budget).AppendStep(toolStep);

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_SensorCostAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxCost = 1m };
        var state = EmptyState(budget) with { SensorCost = 1m };

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_SensorUsageAtLimit_ReturnsExhausted()
    {
        var budget = Generous with { MaxContextTokens = 100 };
        var state = EmptyState(budget) with { SensorUsage = new Usage(60, 40) };

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxContextTokens", result.Reason);
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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }
}
