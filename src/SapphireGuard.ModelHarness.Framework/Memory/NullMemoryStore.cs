namespace SapphireGuard.ModelHarness.Framework.Memory;

/// <summary>
/// No-op memory store. Replace with an <see cref="IMemoryStore"/> implementation
/// backed by a real retrieval system.
/// </summary>
public sealed class NullMemoryStore : IMemoryStore
{
    public Task<IReadOnlyList<string>> RetrieveAsync(string query, int maxSnippets, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
