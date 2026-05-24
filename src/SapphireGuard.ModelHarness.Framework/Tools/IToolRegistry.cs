namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Holds the tools available to an agent and dispatches model-issued calls.
/// The default implementation is <c>InMemoryToolRegistry</c>; replace with a custom
/// implementation to source tools dynamically or add cross-cutting dispatch logic.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Returns all registered tools.</summary>
    IReadOnlyList<ITool> List();

    /// <summary>Returns the tool with the given <paramref name="name"/>, or <see langword="null"/> if not registered.</summary>
    ITool? Get(string name);

    /// <summary>
    /// Executes the tool identified by <see cref="ToolCall.ToolName"/>. Returns an error
    /// <see cref="ToolResult"/> if the tool is not found, rather than throwing.
    /// </summary>
    Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct);
}
