using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Guides;

/// <summary>Runs all registered guides in order to produce a populated <see cref="ContextDraft"/>.</summary>
public interface IGuideRunner
{
    Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct);
}
