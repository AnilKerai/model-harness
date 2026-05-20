using AgentHarness.Framework.State;

namespace AgentHarness.Framework.Context;

/// <summary>Returns the trajectory unchanged.</summary>
public sealed class NoopTrajectoryCompactor : ITrajectoryCompactor
{
    public Task<IReadOnlyList<Step>> CompactAsync(AgentState state, CancellationToken ct) =>
        Task.FromResult(state.Trajectory);
}
