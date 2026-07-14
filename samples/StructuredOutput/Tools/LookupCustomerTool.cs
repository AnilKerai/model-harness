using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.StructuredOutput.Tools;

/// <summary>
/// Exists so the run is a real ReAct loop rather than a single turn — the point being that the output
/// contract constrains only the final answer, and leaves the tool-using turns alone.
/// </summary>
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

    private static readonly Dictionary<string, string> Customers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ada@contoso.com"] = "Ada Lovelace — Enterprise plan, customer since 2019, 3 open tickets.",
        ["grace@fabrikam.com"] = "Grace Hopper — Free plan, customer since 2024, no prior tickets."
    };

    public string Name => "lookup_customer";

    public string Description => "Looks up a customer's plan and history by email address.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var email = call.Arguments.GetProperty("email").GetString() ?? string.Empty;

        return Task.FromResult(Customers.TryGetValue(email, out var record)
            ? new ToolResult(call.CallId, record)
            : new ToolResult(call.CallId, $"No customer found for '{email}'.", IsError: true));
    }
}
