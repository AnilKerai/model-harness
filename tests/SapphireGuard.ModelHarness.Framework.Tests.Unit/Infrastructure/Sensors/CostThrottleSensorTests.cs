using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Sensors;

public sealed class CostThrottleSensorTests
{
    private static AgentState State(params decimal[] costs)
    {
        var state = AgentState.NewTask("t", new StateBudget
        {
            MaxTurns = 10, MaxContextTokens = 100_000, MaxCostUsd = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });
        foreach (var cost in costs)
            state = state.AppendStep(new ModelCallStep(
                Guid.NewGuid(), DateTimeOffset.UtcNow, [],
                new ModelResponse { Text = null, ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = cost },
                Usage.Zero, cost));
        return state;
    }

    [Fact]
    public async Task Check_UnderLimit_Passes()
    {
        var sut = new CostThrottleSensor(softLimitUsd: 1m);
        var result = await sut.CheckAsync(HookPoint.PreModelCall, State(0.5m), null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_AtLimit_Intervenes()
    {
        var sut = new CostThrottleSensor(softLimitUsd: 1m);
        var result = await sut.CheckAsync(HookPoint.PreModelCall, State(0.5m, 0.5m), null, CancellationToken.None);
        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_OverLimit_Intervenes()
    {
        var sut = new CostThrottleSensor(softLimitUsd: 0.10m);
        var result = await sut.CheckAsync(HookPoint.PreModelCall, State(0.50m), null, CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("soft limit", result.Reason);
    }

    [Fact]
    public async Task Check_NoModelCallsYet_Passes()
    {
        var sut = new CostThrottleSensor(softLimitUsd: 0.01m);
        var result = await sut.CheckAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_MultipleCalls_SummedCorrectly()
    {
        var sut = new CostThrottleSensor(softLimitUsd: 1m);
        // 0.3 + 0.3 + 0.3 = 0.9 — still under
        var result = await sut.CheckAsync(HookPoint.PreModelCall, State(0.3m, 0.3m, 0.3m), null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }
}
