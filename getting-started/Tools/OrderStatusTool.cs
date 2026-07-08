using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

/// <summary>
/// The one domain tool: look up a customer order. Backed by canned data so the sample
/// needs no database or API key. A tool is the only way the agent acts on the world.
/// </summary>
public sealed class OrderStatusTool : ITool
{
    private static readonly Dictionary<string, (string Status, string Placed, string Email)> Orders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["A1001"] = ("shipped and out for delivery today", "2 July 2026", "jordan.lee@example.com"),
            ["A1002"] = ("still processing while we restock",  "6 July 2026", "sam.rivera@example.com"),
        };

    public string Name => "get_order_status";

    public string Description =>
        "Look up a customer order by its id (for example A1001). Returns the status, the date it was placed, and the customer's email.";

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        { "type": "object", "properties": { "orderId": { "type": "string", "description": "The order id, e.g. A1001" } }, "required": ["orderId"] }
        """).RootElement;

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var orderId = call.Arguments.TryGetProperty("orderId", out var id) ? id.GetString() ?? "" : "";
        return Task.FromResult(Orders.TryGetValue(orderId, out var o)
            ? new ToolResult(call.CallId, $"Order {orderId}: {o.Status}. Placed {o.Placed}. Customer email: {o.Email}.")
            : new ToolResult(call.CallId, $"No order found with id '{orderId}'.", IsError: true));
    }
}
