using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SupportTriage.Tools;

[ExcludeFromCodeCoverage]
public sealed class GetTicketHistoryTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "customer_id": { "type": "string", "description": "The customer ID returned by lookup_customer." }
          },
          "required": ["customer_id"]
        }
        """).RootElement;

    public string Name => "get_ticket_history";
    public string Description => "Retrieve the last 3 support tickets for a customer by their ID.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var customerId = call.Arguments.TryGetProperty("customer_id", out var c) ? c.GetString() ?? "" : "";
        var history = TicketDatabase.GetHistory(customerId);

        if (history.Count == 0)
            return Task.FromResult(new ToolResult(call.CallId, "No previous tickets found."));

        var sb = new StringBuilder();
        foreach (var t in history)
            sb.AppendLine($"{t.Id}: {t.Subject} — {t.Resolution}");

        return Task.FromResult(new ToolResult(call.CallId, sb.ToString().TrimEnd()));
    }
}
