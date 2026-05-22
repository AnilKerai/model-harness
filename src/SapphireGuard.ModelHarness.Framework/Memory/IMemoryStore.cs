namespace SapphireGuard.ModelHarness.Framework.Memory;

/// <summary>
/// Retrieves relevant memory snippets for the current turn. Implement this to
/// back the memory guide with a vector store, knowledge graph, or any retrieval system.
/// </summary>
public interface IMemoryStore
{
    Task<IReadOnlyList<string>> RetrieveAsync(string query, int maxSnippets, CancellationToken ct);
}
