using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Guides;

/// <summary>Runs all registered guides in order to produce a populated <see cref="ContextDraft"/>.</summary>
public interface IGuideRunner
{
    Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct);
}
