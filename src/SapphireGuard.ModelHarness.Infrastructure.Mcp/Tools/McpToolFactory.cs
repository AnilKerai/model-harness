using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol.Client;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Mcp.Tools;

[ExcludeFromCodeCoverage]
public static class McpToolFactory
{
    public static async Task<IReadOnlyList<ITool>> CreateToolsAsync(
        McpClient client,
        CancellationToken ct = default)
    {
        var mcpTools = await client.ListToolsAsync(cancellationToken: ct);
        return mcpTools.Select(t => (ITool)new McpTool(client, t)).ToList();
    }
}
