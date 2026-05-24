using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Filters or reranks the tool list before each model call. Implement this to reduce
/// prompt noise by exposing only tools relevant to the current turn. The default is
/// <c>PassthroughToolSelector</c> (returns all tools unchanged).
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
