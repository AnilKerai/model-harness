using SapphireGuard.Framework.State;
using SapphireGuard.Framework.Tools;

namespace SapphireGuard.Framework.Guides;

/// <summary>Runs all registered guides in order to produce a populated <see cref="ContextDraft"/>.</summary>
public interface IGuideRunner
{
    Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct);
}
