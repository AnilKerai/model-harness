using SapphireGuard.Framework.State;

namespace SapphireGuard.Framework.Guides;

/// <summary>
/// No-op memory guide. Replace with an implementation that calls a vector
/// store, knowledge graph, or any retrieval system to surface relevant
/// snippets into <see cref="ContextDraft.MemorySnippets"/>.
/// </summary>
public sealed class MemoryGuide : IGuide
{
    public string Name => "memory";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct) =>
        Task.CompletedTask;
}
