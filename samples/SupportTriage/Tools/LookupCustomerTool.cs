using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SupportTriage.Tools;

[ExcludeFromCodeCoverage]
public sealed class LookupCustomerTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "email": { "type": "string", "description": "The customer's email address." }
          },
          "required": ["email"]
        }
        """).RootElement;

    public string Name => "lookup_customer";
    public string Description => "Look up a customer account by email. Returns their tier, account age, and open ticket count. Returns not_found if the email is unknown.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var email = call.Arguments.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        var customer = TicketDatabase.FindCustomer(email);

        var content = customer is null
            ? "not_found"
            : $"id={customer.Id} tier={customer.Tier} account_age_days={customer.AccountAgeDays} open_tickets={customer.OpenTickets}";

        return Task.FromResult(new ToolResult(call.CallId, content));
    }
}
