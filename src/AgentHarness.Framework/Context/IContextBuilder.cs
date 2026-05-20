using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Context;

/// <summary>Materialises the next prompt the model will see from the current agent state.</summary>
public interface IContextBuilder
{
    Task<IReadOnlyList<Message>> BuildAsync(
        AgentState state,
        IReadOnlyList<ITool> availableTools,
        CancellationToken ct);
}
