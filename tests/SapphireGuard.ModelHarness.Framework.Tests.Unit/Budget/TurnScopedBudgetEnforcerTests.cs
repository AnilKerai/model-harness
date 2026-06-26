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
        MaxContextTokens = 100_000,
        MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    };

    private static AgentState EmptyState(StateBudget? budget = null) =>
        AgentState.NewTask("test", budget ?? Generous);

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
            .WithUserMessage("turn 2").AppendStep(ModelStep()).AppendStep(ModelStep())
            .WithUserMessage("turn 3").AppendStep(ModelStep()).AppendStep(ModelStep());

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_TurnsAtLimitWithinSingleTurn_ReturnsExhausted()
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
    public void Check_CostResetsAtUserTurn_OnlyCurrentTurnCounts()
    {
        var budget = Generous with { MaxCost = 1m };
        // 0.9 spent last turn would exhaust cumulatively; per-turn it resets to 0.4.
        var state = EmptyState(budget)
            .AppendStep(ModelStep(cost: 0.9m))
            .WithUserMessage("turn 2")
            .AppendStep(ModelStep(cost: 0.4m));

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.False(result.IsExhausted);
    }

    [Fact]
    public void Check_CostAtLimitWithinTurn_ReturnsExhausted()
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
    public void Check_TokensResetAtUserTurn_OnlyCurrentTurnCounts()
    {
        var budget = Generous with { MaxContextTokens = 100 };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(inputTokens: 60, outputTokens: 30))
            .WithUserMessage("turn 2")
            .AppendStep(ModelStep(inputTokens: 10, outputTokens: 10));

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

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

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCost", result.Reason);
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
}
