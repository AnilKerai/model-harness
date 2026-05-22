using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Context;

/// <summary>
/// Runs the guide pipeline and assembles its output into a prompt. This is
/// the boundary between the guide layer (perception shaping) and the model
/// layer (inference).
/// </summary>
public interface IContextBuilder
{
    Task<ContextBuildResult> BuildAsync(
        AgentState state,
        IReadOnlyList<ITool> allTools,
        CancellationToken ct);
}
