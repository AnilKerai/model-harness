using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Filters or reranks the tool list before each model call. Implement this to reduce
/// prompt noise by exposing only tools relevant to the current turn. The default is
/// <c>PassthroughToolSelector</c> (returns all tools unchanged). The selection is enforced, not
/// advisory: the loop dispatches only calls to tools the model was sent on that call, so a tool dropped
/// here cannot run on that turn even if the model names it.
/// <para>
/// A selector that throws fails closed: the model is offered no tools for that turn (the run continues and
/// the error is logged as a guide error). Only genuine cancellation of the harness token propagates. A
/// selector that would rather keep every tool than lose them all (a relevance ranker, say) must catch its own
/// errors and return the tools it was given.
/// </para>
/// </summary>
public interface IToolSelector
{
    /// <summary>
    /// Returns the subset (or reordering) of <paramref name="tools"/> to expose to the
    /// model on this turn. The returned list replaces <c>ContextDraft.AvailableTools</c>
    /// for the upcoming model call.
    /// </summary>
    Task<IReadOnlyList<ITool>> SelectAsync(IReadOnlyList<ITool> tools, AgentState state, CancellationToken ct);
}
