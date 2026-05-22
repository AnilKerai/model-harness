using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>
/// Observes the loop at registered hookpoints and may intervene by
/// returning <see cref="SensorResult.Intervene"/>.
/// </summary>
public interface ISensor
{
    string Name { get; }
    IReadOnlySet<HookPoint> HookPoints { get; }

    Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct);
}
