using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Filters or reranks the tool list before each model call. Implement this to
/// reduce prompt noise by exposing only tools relevant to the current turn.
/// </summary>
public interface IToolSelector
{
    Task<IReadOnlyList<ITool>> SelectAsync(IReadOnlyList<ITool> tools, AgentState state, CancellationToken ct);
}
