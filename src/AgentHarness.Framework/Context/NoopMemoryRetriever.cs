using AgentHarness.Framework.State;

namespace AgentHarness.Framework.Context;

/// <summary>Returns no memory snippets.</summary>
public sealed class NoopMemoryRetriever : IMemoryRetriever
{
    public Task<IReadOnlyList<string>> RetrieveAsync(AgentState state, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
