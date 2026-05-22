using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Filters or reranks <see cref="ContextDraft.AvailableTools"/> via <see cref="IToolSelector"/>
/// before each model call. Replace <see cref="IToolSelector"/> with an implementation
/// that ranks by relevance, applies policy, or limits tool count; the default is
/// <see cref="PassthroughToolSelector"/> (all tools, unchanged).
/// </summary>
public sealed class ToolSelectorGuide(IToolSelector selector) : IGuide
{
    public string Name => "tool-selector";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var selected = await selector.SelectAsync(draft.AvailableTools, state, ct);
        draft.AvailableTools = selected.ToList();
    }
}
