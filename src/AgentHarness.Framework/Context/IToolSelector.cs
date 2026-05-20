using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Context;

/// <summary>
/// Filters or re-orders the registered tools for a single turn. The default
/// passthrough exposes everything; downstream implementations can implement
/// relevance ranking or per-phase gating.
/// </summary>
public interface IToolSelector
{
    Task<IReadOnlyList<ITool>> SelectAsync(AgentState state, IReadOnlyList<ITool> registered, CancellationToken ct);
}
