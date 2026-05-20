using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Context;

/// <summary>Exposes every registered tool unchanged.</summary>
public sealed class PassthroughToolSelector : IToolSelector
{
    public Task<IReadOnlyList<ITool>> SelectAsync(AgentState state, IReadOnlyList<ITool> registered, CancellationToken ct) =>
        Task.FromResult(registered);
}
