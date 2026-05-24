namespace SapphireGuard.ModelHarness.Framework.Memory;

/// <summary>
/// Retrieves relevant memory snippets for the current turn. Implement this to back
/// the memory guide with a vector store, knowledge graph, or any retrieval system.
/// The default is <c>NullMemoryStore</c> (returns nothing).
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Returns up to <paramref name="maxSnippets"/> text snippets relevant to
    /// <paramref name="query"/>. Snippets are injected into the model's context
    /// by <c>MemoryGuide</c> before each model call.
    /// </summary>
    Task<IReadOnlyList<string>> RetrieveAsync(string query, int maxSnippets, CancellationToken ct);
}
