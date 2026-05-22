using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Blocks model calls once accumulated spend for the current task exceeds a
/// soft threshold. Complements the hard <see cref="Framework.Budget.IBudgetEnforcer"/>
/// limit — set this lower to give the model a warning turn ("wrap up, you're
/// running expensive") before the hard stop cuts the run short.
/// </summary>
public sealed class CostThrottleSensor(decimal softLimitUsd) : ISensor
{
    public string Name => "cost-throttle";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreModelCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var spent = state.Trajectory
            .OfType<ModelCallStep>()
            .Sum(s => s.Cost);

        if (spent >= softLimitUsd)
            return Task.FromResult(SensorResult.Block(
                $"Task spend ${spent:F4} has reached the ${softLimitUsd:F4} soft limit. " +
                "Produce your final answer now using only what you already know."));

        return Task.FromResult(SensorResult.Pass);
    }
}
