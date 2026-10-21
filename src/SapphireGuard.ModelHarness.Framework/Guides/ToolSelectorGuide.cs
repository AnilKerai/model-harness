using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Filters or reranks <see cref="ContextDraft.AvailableTools"/> via <see cref="IToolSelector"/>
/// before each model call. Replace <see cref="IToolSelector"/> with an implementation
/// that ranks by relevance, applies policy, or limits tool count; the default is
/// <see cref="PassthroughToolSelector"/> (all tools, unchanged).
/// <para>
/// Unlike other supporting guides this one fails closed: the selection is the dispatch allowlist, so if the
/// selector throws (its own timeout included) the guide empties <see cref="ContextDraft.AvailableTools"/> and
/// rethrows. The model is offered no tools for that turn, the run continues, and
/// <see cref="DefaultGuideRunner"/> logs the error. Only genuine cancellation of the harness token propagates
/// untouched. A selector that prefers availability over enforcement catches its own errors and returns the
/// tools it was given.
/// </para>
/// </summary>
public sealed class ToolSelectorGuide(IToolSelector selector) : IGuide
{
    public string Name => "tool-selector";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        try
        {
            var selected = await selector.SelectAsync(draft.AvailableTools, state, ct);
            draft.AvailableTools = selected.ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            draft.AvailableTools = [];
            throw;
        }
    }
}
