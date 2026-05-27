using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Renders the agent trajectory into the context draft. Intentionally separate from
/// <see cref="IGuide"/> so the runner can guarantee it executes after all supporting
/// guides without relying on DI registration order. Swap the default implementation
/// via <c>builder.WithTrajectoryGuide&lt;T&gt;()</c>.
/// </summary>
public interface ITrajectoryGuide
{
    string Name { get; }

    Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct);
}
