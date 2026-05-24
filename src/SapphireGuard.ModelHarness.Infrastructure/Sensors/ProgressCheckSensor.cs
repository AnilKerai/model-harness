using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Annotates the trajectory every N model turns (configurable; default 5) with a structured
/// checkpoint prompt, asking the model to verify it is still working toward the original
/// goal before continuing. Addresses context drift and compounding error accumulation
/// on long-running tasks.
/// </summary>
public sealed class ProgressCheckSensor(int interval = 5) : ISensor
{
    public string Name => "progress-check";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreModelCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var completedTurns = state.Trajectory.OfType<ModelCallStep>().Count();

        if (completedTurns > 0 && completedTurns % interval == 0)
            return Task.FromResult(SensorResult.Intervene(
                $"You have completed {completedTurns} turns. Before continuing: verify you are still " +
                "working toward your original goal, and if you already have enough information to answer, do so now."));

        return Task.FromResult(SensorResult.Pass);
    }
}
