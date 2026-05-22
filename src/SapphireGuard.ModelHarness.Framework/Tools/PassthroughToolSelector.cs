using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Returns all tools unchanged. Replace with an <see cref="IToolSelector"/>
/// implementation that filters or reranks based on task context.
/// </summary>
public sealed class PassthroughToolSelector : IToolSelector
{
    public Task<IReadOnlyList<ITool>> SelectAsync(IReadOnlyList<ITool> tools, AgentState state, CancellationToken ct) =>
        Task.FromResult(tools);
}
