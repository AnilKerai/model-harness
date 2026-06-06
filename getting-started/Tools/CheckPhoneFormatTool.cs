using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

public sealed class CheckPhoneFormatTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "phone_number": {
              "type": "string",
              "description": "The telephone number to validate."
            },
            "jurisdiction": {
              "type": "string",
              "description": "The company's registered jurisdiction (e.g. 'England and Wales', 'Scotland'). Used to select the correct format rules."
            }
          },
          "required": ["phone_number", "jurisdiction"]
        }
        """).RootElement;

    private static readonly string[] UkJurisdictionKeywords =
        ["england", "wales", "scotland", "northern ireland", "united kingdom", "uk", "great britain"];

    public string Name => "check_phone_format";
    public string Description => "Deterministic check: validates that the telephone number is well-formed for the company's jurisdiction. For UK companies checks digit count and prefix. Returns a structured verdict — use it directly.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var phone        = call.Arguments.TryGetProperty("phone_number", out var p) ? p.GetString() ?? "" : "";
        var jurisdiction = call.Arguments.TryGetProperty("jurisdiction",  out var j) ? j.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(phone))
            return CheckResult.Build(call.CallId, "Inconclusive", "Medium", "No telephone number provided.");

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        // Normalise +44 international prefix to leading 0.
        if (phone.TrimStart().StartsWith("+44") && digits.Length == 12)
            digits = "0" + digits[2..];

        var isUk = UkJurisdictionKeywords.Any(k =>
            jurisdiction.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (!isUk)
        {
            // E.164 allows 7–15 digits; anything outside that is clearly invalid.
            return digits.Length is >= 7 and <= 15
                ? CheckResult.Build(call.CallId, "Pass", "Medium",
                    $"Phone number format appears valid for '{jurisdiction}' ({digits.Length} digits).")
                : CheckResult.Build(call.CallId, "Fail", "Medium",
                    $"'{phone}' has {digits.Length} digit(s) after stripping formatting, which is outside the valid range (7–15) for '{jurisdiction}'.");
        }

        if (digits.Length != 11)
            return CheckResult.Build(call.CallId, "Fail", "High",
                $"UK phone numbers must be 11 digits; '{phone}' has {digits.Length} digit(s) after stripping formatting characters.");

        if (!digits.StartsWith('0'))
            return CheckResult.Build(call.CallId, "Fail", "High",
                $"UK phone numbers must begin with 0 (or +44); '{phone}' does not.");

        var prefix2 = digits[..2];
        var prefix3 = digits[..3];

        return prefix2 switch
        {
            "01" or "02" => CheckResult.Build(call.CallId, "Pass", "High",
                $"'{phone}' is a valid UK geographic landline ({prefix3}x prefix)."),
            "03" => CheckResult.Build(call.CallId, "Pass", "High",
                $"'{phone}' is a valid UK non-geographic landline ({prefix3} prefix)."),
            "07" => CheckResult.Build(call.CallId, "Inconclusive", "Medium",
                $"'{phone}' is a UK mobile number — unusual for an AP contact but not a hard red flag."),
            "08" or "09" => CheckResult.Build(call.CallId, "Inconclusive", "Medium",
                $"'{phone}' is a UK special-rate number ({prefix3} prefix) — may not be a standard business line."),
            _ => CheckResult.Build(call.CallId, "Fail", "High",
                $"'{phone}' does not match any recognised UK phone number prefix.")
        };
    }
}
