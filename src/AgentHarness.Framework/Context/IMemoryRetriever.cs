using AgentHarness.Framework.State;

namespace AgentHarness.Framework.Context;

/// <summary>
/// Retrieves long-term memory snippets to inject into the prompt. The default
/// no-op returns no snippets.
/// </summary>
public interface IMemoryRetriever
{
    Task<IReadOnlyList<string>> RetrieveAsync(AgentState state, CancellationToken ct);
}
