using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Runs supporting guides sequentially in registration order, then the trajectory guide last.
/// Sequential execution lets each guide build on contributions from the ones before it.
/// <see cref="ITrajectoryGuide"/> is always last so it can measure all prior contributions
/// and compute an accurate token budget — enforced by type, not by DI registration order.
/// </summary>
public sealed class DefaultGuideRunner(IEnumerable<IGuide> guides, ITrajectoryGuide trajectoryGuide) : IGuideRunner
{
    private readonly IReadOnlyList<IGuide> _guides = [.. guides];

    public async Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct)
    {
        var draft = new ContextDraft { AvailableTools = [.. allTools] };

        foreach (var guide in _guides)
        {
            ct.ThrowIfCancellationRequested();
            await guide.ContributeAsync(draft, state, ct);
        }

        ct.ThrowIfCancellationRequested();
        await trajectoryGuide.ContributeAsync(draft, state, ct);

        return draft;
    }
}
