using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

public sealed class CheckApEmailPatternTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "email": {
              "type": "string",
              "description": "The contact email address to validate."
            }
          },
          "required": ["email"]
        }
        """).RootElement;

    // Local-parts that unambiguously indicate an AP/finance inbox.
    private static readonly string[] FinanceKeywords =
    [
        "invoices", "invoice", "accountspayable", "accounts-payable", "accounts.payable",
        "ap", "finance", "financial", "payments", "payment", "billing", "bills",
        "remittance", "remit", "creditors", "accounts", "payables"
    ];

    // Local-parts that indicate a sales, marketing, or general enquiries inbox — not AP.
    private static readonly string[] NonApKeywords =
    [
        "sales", "marketing", "hello", "info", "enquiries", "enquiry", "contact",
        "support", "help", "press", "media", "pr", "partnerships", "business"
    ];

    public string Name => "check_ap_email_pattern";
    public string Description => "Deterministic check: inspects the local-part of the email address to determine whether it matches known AP/finance patterns or known non-AP patterns. Returns a structured verdict — use it directly.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var email = call.Arguments.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";

        var atIdx = email.IndexOf('@');
        if (atIdx < 0)
            return CheckResult.Build(call.CallId, "Inconclusive", "Low", $"'{email}' is not a valid email address.");

        var localPart    = email[..atIdx];
        var normalised   = localPart.ToLowerInvariant().Replace(".", "").Replace("-", "").Replace("_", "");

        var financeMatch = FinanceKeywords.FirstOrDefault(k =>
            normalised.Contains(k.Replace("-", "").Replace(".", "")));

        if (financeMatch is not null)
            return CheckResult.Build(call.CallId, "Pass", "High",
                $"Local-part '{localPart}' matches known AP/finance pattern ('{financeMatch}').");

        var nonApMatch = NonApKeywords.FirstOrDefault(k => normalised.Contains(k));

        if (nonApMatch is not null)
            return CheckResult.Build(call.CallId, "Fail", "High",
                $"Local-part '{localPart}' matches a known non-AP pattern ('{nonApMatch}') — this is a sales, marketing, or general enquiries inbox, not an AP contact.");

        return CheckResult.Build(call.CallId, "Inconclusive", "Medium",
            $"Local-part '{localPart}' does not match known AP/finance or non-AP patterns; manual review recommended.");
    }
}
