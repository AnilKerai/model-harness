using System.Text.Json;
using SapphireGuard.Framework.Tools;

namespace SapphireGuard.Infrastructure.Tools;

/// <summary>Returns the supplied <c>text</c> argument unchanged.</summary>
public sealed class EchoTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Text to echo back." }
          },
          "required": ["text"]
        }
        """).RootElement;

    public string Name => "echo";

    public string Description => "Echoes the given text back verbatim.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var text = call.Arguments.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return Task.FromResult(new ToolResult(call.CallId, text));
    }
}
