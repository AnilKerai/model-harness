using SapphireGuard.Framework.State;

namespace SapphireGuard.Framework.Sensors;

/// <summary>
/// Observes the loop at registered hookpoints and may block transitions by
/// returning <see cref="SensorResult.Block"/>.
/// </summary>
public interface ISensor
{
    string Name { get; }
    IReadOnlySet<HookPoint> HookPoints { get; }

    Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct);
}
