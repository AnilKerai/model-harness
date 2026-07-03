using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class ProgressCheckSensorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 100, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep ModelStep() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn,
                Usage = Usage.Zero, Cost = 0m
            },
            Usage: Usage.Zero, Cost: 0m);

    private static AgentState WithTurns(int count)
    {
        var state = EmptyState();
        for (var i = 0; i < count; i++)
            state = state.AppendStep(ModelStep());
        return state;
    }

    private static readonly ProgressCheckSensor Sut = new(interval: 5);

    [Fact]
    public async Task Check_NoTurnsCompleted_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PreModelCall, EmptyState(), null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_TurnsBelowInterval_Passes()
    {
        var state = WithTurns(4);
        var result = await Sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_TurnsAtInterval_Intervenes()
    {
        var state = WithTurns(5);
        var result = await Sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("5", result.Reason);
    }

    [Fact]
    public async Task Check_TurnsBetweenIntervals_Passes()
    {
        var state = WithTurns(7);
        var result = await Sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_TurnsAtSecondInterval_Intervenes()
    {
        var state = WithTurns(10);
        var result = await Sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("10", result.Reason);
    }

    [Fact]
    public async Task Check_CustomInterval_RespectsIt()
    {
        var sut = new ProgressCheckSensor(interval: 3);
        var state = WithTurns(3);
        var result = await sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_OnlyCountsModelCallSteps()
    {
        // Non-ModelCallStep entries in the trajectory should not affect the count.
        var toolStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            new Tools.ToolCall(Guid.NewGuid().ToString("n"), "search",
                System.Text.Json.JsonDocument.Parse("{}").RootElement),
            new Tools.ToolResult("id", "result"));

        var state = WithTurns(4).AppendStep(toolStep);

        var result = await Sut.CheckAsync(HookPoint.PreModelCall, state, null, CancellationToken.None);
        Assert.False(result.IsIntervene); // still only 4 model turns
    }
}
