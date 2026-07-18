using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Budget;

public sealed class TurnScopedBudgetEnforcerTests
{
    private static readonly StateBudget Generous = new()
    {
        MaxTurns = 3,
        MaxTotalTokens = 100_000,
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

    private static readonly TurnScopedBudgetEnforcer Sut = new();

    [Fact]
    public void Check_ModelCallsSpreadAcrossTurns_DoesNotExhaust()
    {
        // 6 model calls total, but only 2 per user turn — under MaxTurns (3) each turn.
        var budget = Generous with { MaxTurns = 3 };
        var state = EmptyState(budget) // NewTask seeds the first UserMessageStep
            .AppendStep(ModelStep()).AppendStep(ModelStep())
            .WithUserMessage("turn 2", DateTimeOffset.UtcNow).AppendStep(ModelStep()).AppendStep(ModelStep())
            .WithUserMessage("turn 3", DateTimeOffset.UtcNow).AppendStep(ModelStep()).AppendStep(ModelStep());

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_TurnsAtLimitWithinSingleTurn_ReturnsExhausted()
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
    public void Check_CostResetsAtUserTurn_OnlyCurrentTurnCounts()
    {
        var budget = Generous with { MaxCost = 1m };
        // 0.9 spent last turn would exhaust cumulatively; per-turn it resets to 0.4.
        var state = EmptyState(budget)
            .AppendStep(ModelStep(cost: 0.9m))
            .WithUserMessage("turn 2", DateTimeOffset.UtcNow)
            .AppendStep(ModelStep(cost: 0.4m));

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_CostAtLimitWithinTurn_ReturnsExhausted()
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
    public void Check_TokensResetAtUserTurn_OnlyCurrentTurnCounts()
    {
        var budget = Generous with { MaxTotalTokens = 100 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(inputTokens: 60, outputTokens: 30))
            .WithUserMessage("turn 2", DateTimeOffset.UtcNow)
            .AppendStep(ModelStep(inputTokens: 10, outputTokens: 10));

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_DelegatedToolCostWithinTurn_Counts()
    {
        var budget = Generous with { MaxCost = 1m };
        var state = EmptyState(budget)
            .AppendStep(new ToolCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new Tools.ToolCall("id", "agent", System.Text.Json.JsonDocument.Parse("{}").RootElement),
                new Tools.ToolResult("id", "ok", Cost: 1.0m)));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
    }

    [Fact]
    public void Check_WallClockExceeded_ReturnsExhausted()
    {
        var budget = Generous with { MaxWallClock = TimeSpan.FromMilliseconds(1) };
        // The latest user message is the per-turn wall-clock anchor — ten seconds ago.
        var state = AgentState.NewTask("test", budget, DateTimeOffset.UtcNow.AddSeconds(-10));

        var result = Sut.Check(state);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxWallClock", result.Reason);
    }

    [Fact]
    public void Check_WallClock_ResetsAtLatestUserTurn()
    {
        // The defining per-turn property: the first turn is 10s old (would blow a 1s cumulative
        // budget), but the latest user turn is fresh, so wall-clock resets and is NOT exhausted.
        // DefaultBudgetEnforcer, anchored to the first message, would exhaust here.
        var budget = Generous with { MaxWallClock = TimeSpan.FromSeconds(1) };
        var state = AgentState.NewTask("test", budget, DateTimeOffset.UtcNow.AddSeconds(-10))
            .AppendStep(ModelStep())
            .WithUserMessage("fresh turn", DateTimeOffset.UtcNow);

        var result = Sut.Check(state);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_Lookahead_CountsTowardPerTurnWallClock()
    {
        var budget = Generous with { MaxWallClock = TimeSpan.FromSeconds(2) };
        var state = AgentState.NewTask("test", budget, DateTimeOffset.UtcNow.AddSeconds(-1)); // ~1s into the turn

        Assert.False(Sut.Check(state).IsExhausted);                                     // 1s < 2s
        Assert.True(Sut.Check(state, lookahead: TimeSpan.FromSeconds(2)).IsExhausted);  // 1s + 2s >= 2s
    }
}
