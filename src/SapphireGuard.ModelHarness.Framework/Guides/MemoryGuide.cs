using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Surfaces relevant memory snippets into <see cref="ContextDraft.MemorySnippets"/>
/// by querying <see cref="IMemoryStore"/> with the latest user turn — the message the
/// agent is actually responding to. In a single-task run that is the seeded task text;
/// in a multi-turn conversation it tracks the current question rather than anchoring on
/// the opener. Replace <see cref="IMemoryStore"/> with a vector store, knowledge graph,
/// or any retrieval system; the default is <see cref="NullMemoryStore"/> (no-op).
/// </summary>
public sealed class MemoryGuide(IMemoryStore store, int maxSnippets = 5) : IGuide
{
    public string Name => "memory";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var query = state.Trajectory.OfType<UserMessageStep>().LastOrDefault()?.Content ?? state.TaskText;
        var snippets = await store.RetrieveAsync(query, maxSnippets, ct);
        draft.MemorySnippets.AddRange(snippets);
    }
}
