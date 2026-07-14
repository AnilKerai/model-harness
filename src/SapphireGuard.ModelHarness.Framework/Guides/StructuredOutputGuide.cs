using SapphireGuard.ModelHarness.Framework.Output;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// States the run's output contract in the system prompt, every turn.
/// <para>
/// Contributes a <see cref="ContextDraft.SystemSections"/> entry rather than a trajectory message. That
/// placement is load-bearing: system sections are rebuilt from scratch on every turn and are never
/// reachable by <see cref="HeadEvictionTrajectoryGuide"/>, which only ever trims the trajectory. So the
/// sensor enforcing this contract can never reject an answer whose contract was quietly compacted away.
/// </para>
/// </summary>
/// <typeparam name="T">The type the run's final answer must bind to.</typeparam>
public sealed class StructuredOutputGuide<T>(StructuredOutputContract<T> contract) : IGuide
{
    public string Name => "structured-output";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemSections.Add(contract.SystemSection);
        return Task.CompletedTask;
    }
}
