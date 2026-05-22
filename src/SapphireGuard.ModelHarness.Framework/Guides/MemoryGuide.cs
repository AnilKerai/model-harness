using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Surfaces relevant memory snippets into <see cref="ContextDraft.MemorySnippets"/>
/// by querying <see cref="IMemoryStore"/> with the current task text.
/// Replace <see cref="IMemoryStore"/> with a vector store, knowledge graph, or any
/// retrieval system; the default is <see cref="NullMemoryStore"/> (no-op).
/// </summary>
public sealed class MemoryGuide(IMemoryStore store, int maxSnippets = 5) : IGuide
{
    public string Name => "memory";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var snippets = await store.RetrieveAsync(state.TaskText, maxSnippets, ct);
        draft.MemorySnippets.AddRange(snippets);
    }
}
