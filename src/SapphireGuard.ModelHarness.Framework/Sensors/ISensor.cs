using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>
/// Observes the loop at declared hookpoints and may raise a concern by returning
/// <see cref="SensorResult.Intervene"/>. Sensors run in parallel at each hookpoint;
/// the loop's response to an intervention depends on which hookpoint fired — see
/// <see cref="HookPoint"/> for the per-hookpoint semantics.
/// </summary>
public interface ISensor
{
    /// <summary>Unique identifier used in traces and diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// The hookpoints this sensor wants to observe. <see cref="CheckAsync"/> is only
    /// called at hookpoints declared here.
    /// </summary>
    IReadOnlySet<HookPoint> HookPoints { get; }

    /// <summary>
    /// Evaluates the current loop state at <paramref name="hookPoint"/> and returns
    /// <see cref="SensorResult.Pass"/> or <see cref="SensorResult.Intervene"/>.
    /// <paramref name="triggeringStep"/> is the step that caused the hookpoint to fire
    /// (e.g. the <see cref="State.ToolCallStep"/> at <see cref="HookPoint.PreToolCall"/>),
    /// or <see langword="null"/> at hookpoints not tied to a specific step.
    /// </summary>
    Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct);
}
