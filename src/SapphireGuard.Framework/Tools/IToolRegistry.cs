namespace SapphireGuard.Framework.Tools;

/// <summary>Holds the tools available to an agent and dispatches model-issued calls.</summary>
public interface IToolRegistry
{
    IReadOnlyList<ITool> List();

    ITool? Get(string name);

    Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct);
}
