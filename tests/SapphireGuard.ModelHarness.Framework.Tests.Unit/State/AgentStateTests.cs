using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.State;

public sealed class AgentStateTests
{
    private static readonly StateBudget Budget = new()
    {
        MaxTurns = 1,
        MaxTotalTokens = 1,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewTask_WithoutTaskId_GeneratesUniqueIds()
    {
        var a = AgentState.NewTask("t", Budget, T0);
        var b = AgentState.NewTask("t", Budget, T0);

        Assert.False(string.IsNullOrWhiteSpace(a.TaskId));
        Assert.NotEqual(a.TaskId, b.TaskId);
    }

    [Fact]
    public void NewTask_WithTaskId_UsesItVerbatim()
    {
        var state = AgentState.NewTask("t", Budget, T0, taskId: "ext-42");

        Assert.Equal("ext-42", state.TaskId);
    }
}
