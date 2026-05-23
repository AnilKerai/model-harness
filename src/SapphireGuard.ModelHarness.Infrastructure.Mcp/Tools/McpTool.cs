using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Mcp.Tools;

[ExcludeFromCodeCoverage]
public sealed class McpTool(McpClient client, McpClientTool mcpTool) : ITool
{
    public string Name => mcpTool.Name;
    public string Description => mcpTool.Description ?? string.Empty;
    public JsonElement InputSchema => mcpTool.JsonSchema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var args = call.Arguments.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);

        var result = await client.CallToolAsync(mcpTool.Name, args, cancellationToken: ct);

        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(b => b.Text));

        return new ToolResult(call.CallId,
            string.IsNullOrEmpty(text) ? "(no text content)" : text,
            IsError: result.IsError == true);
    }
}
