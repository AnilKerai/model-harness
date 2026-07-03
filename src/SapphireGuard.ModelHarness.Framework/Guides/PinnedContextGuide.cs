using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Renders <see cref="AgentState.Pins"/> into <see cref="ContextDraft.SystemSections"/> — the
/// persistent, non-evictable context region — every turn. This is how reference content a tool loaded
/// (a procedure, an output contract, a spec, via <see cref="Tools.ToolResult.Pins"/>) stays verbatim
/// in context past trajectory eviction and compaction. Runs before the trajectory guide so the pinned
/// sections are measured against the eviction window like any other system content.
/// </summary>
public sealed class PinnedContextGuide : IGuide
{
    public string Name => "pinned-context";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        foreach (var pin in state.Pins)
            draft.SystemSections.Add($"# {pin.Label}\n{pin.Content}");
        return Task.CompletedTask;
    }
}
