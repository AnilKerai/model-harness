using SapphireGuard.Framework.State;

namespace SapphireGuard.Framework.Guides;

/// <summary>
/// Passthrough tool selector — exposes all registered tools unchanged.
/// Replace with an implementation that filters or reorders
/// <see cref="ContextDraft.AvailableTools"/> based on task context, turn count,
/// or relevance ranking.
/// </summary>
public sealed class ToolSelectorGuide : IGuide
{
    public string Name => "tool-selector";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct) =>
        Task.CompletedTask;
}
