using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SupportTriage.Tools;

[ExcludeFromCodeCoverage]
public sealed class DraftResponseTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "ticket_id":  { "type": "string", "description": "The ticket ID being responded to." },
            "body":       { "type": "string", "description": "The response body to send to the customer." }
          },
          "required": ["ticket_id", "body"]
        }
        """).RootElement;

    public string Name => "draft_response";
    public string Description => "Record and send a response to the customer for the given ticket.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var ticketId = call.Arguments.TryGetProperty("ticket_id", out var t) ? t.GetString() ?? "" : "";
        var body = call.Arguments.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        var bar = new string('─', 60);
        Console.WriteLine();
        Console.WriteLine($"  ┌ Response sent for {ticketId}");
        Console.WriteLine($"  │ {body.Replace("\n", "\n  │ ")}");
        Console.WriteLine($"  └{bar[..^2]}");

        return Task.FromResult(new ToolResult(call.CallId, $"Response recorded for {ticketId}."));
    }
}
