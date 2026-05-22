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
        MaxCostUsd = 10m,
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
        var budget = Generous with { MaxCostUsd = 1m };
        var state = EmptyState(budget)
            .AppendStep(ModelStep(cost: 0.5m))
            .AppendStep(ModelStep(cost: 0.5m));

        var result = Sut.Check(state, DateTimeOffset.UtcNow);

        Assert.True(result.IsExhausted);
        Assert.Contains("MaxCostUsd", result.Reason);
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
}
