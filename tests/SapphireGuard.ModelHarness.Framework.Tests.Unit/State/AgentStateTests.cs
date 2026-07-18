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

    [Fact]
    public void TotalSpend_CountsSensorCompactionAndDelegatedToolSpend_NotJustModelCalls()
    {
        // The whole point of hoisting this: a walk over ModelCallStep alone misses three real spend
        // sources. AgentTool used to do exactly that and reported a sub-agent's cost low to its parent.
        var state = (AgentState.NewTask("t", Budget, T0)
                .AppendStep(ModelStep(new Usage(100, 50), 0.10m))
                .AppendStep(DelegatingToolStep(new Usage(200, 80), 0.25m)))
            with
            {
                SensorUsage = new Usage(10, 5), SensorCost = 0.01m,
                CompactionUsage = new Usage(20, 10), CompactionCost = 0.02m
            };

        var (turns, usage, cost) = state.TotalSpend();

        Assert.Equal(1, turns);                    // only the model call is a turn
        Assert.Equal(330, usage.InputTokens);      // 100 model + 200 delegated + 10 sensor + 20 compaction
        Assert.Equal(145, usage.OutputTokens);     // 50 + 80 + 5 + 10
        Assert.Equal(0.38m, cost);                 // 0.10 + 0.25 + 0.01 + 0.02
    }

    [Fact]
    public void TotalSpend_ToolResultWithoutSpend_ContributesNothing()
    {
        // Cost/Usage are nullable on ToolResult — an ordinary (non-delegating) tool reports neither.
        var state = AgentState.NewTask("t", Budget, T0)
            .AppendStep(ModelStep(new Usage(10, 5), 0.01m))
            .AppendStep(DelegatingToolStep(usage: null, cost: null));

        var (_, usage, cost) = state.TotalSpend();

        Assert.Equal(15, usage.TotalTokens);
        Assert.Equal(0.01m, cost);
    }

    private static ModelCallStep ModelStep(Usage usage, decimal cost) =>
        new(Guid.NewGuid(), T0,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = usage, Cost = cost
            },
            Usage: usage, Cost: cost);

    private static ToolCallStep DelegatingToolStep(Usage? usage, decimal? cost) =>
        new(Guid.NewGuid(), T0,
            Call: new Framework.Tools.ToolCall("call-1", "sub-agent",
                System.Text.Json.JsonDocument.Parse("{}").RootElement),
            Result: new Framework.Tools.ToolResult("call-1", "delegated answer", Cost: cost, Usage: usage));
}
