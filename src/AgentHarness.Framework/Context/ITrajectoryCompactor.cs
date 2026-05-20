using AgentHarness.Framework.State;

namespace AgentHarness.Framework.Context;

/// <summary>
/// Shrinks long trajectories before they are rendered into the prompt. The
/// default no-op returns the trajectory unchanged.
/// </summary>
public interface ITrajectoryCompactor
{
    Task<IReadOnlyList<Step>> CompactAsync(AgentState state, CancellationToken ct);
}
