using AgentHarness.Framework.Tools;

namespace AgentHarness.Infrastructure.Tools;

/// <summary>Stores tools in a dictionary keyed by <see cref="ITool.Name"/>.</summary>
public sealed class InMemoryToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly IReadOnlyList<ITool> _ordered;

    public InMemoryToolRegistry(IEnumerable<ITool> tools)
    {
        var list = tools.ToArray();
        _tools = list.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _ordered = list;
    }

    public IReadOnlyList<ITool> List() => _ordered;

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public async Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.ToolName, out var tool))
        {
            return new ToolResult(call.CallId, $"Unknown tool '{call.ToolName}'.", IsError: true);
        }

        try
        {
            return await tool.ExecuteAsync(call, context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(call.CallId, $"Tool '{call.ToolName}' threw: {ex.Message}", IsError: true);
        }
    }
}
