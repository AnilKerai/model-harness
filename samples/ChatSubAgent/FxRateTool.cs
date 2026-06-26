using System.Globalization;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.ChatSubAgent;

/// <summary>
/// Fake foreign-exchange rate lookup — the currency agent's domain capability that the
/// general chat agent lacks. Rates are hardcoded (USD-relative) so the sample is
/// deterministic and needs no external API; cross-rates are derived for any listed pair.
/// </summary>
public sealed class FxRateTool : ITool
{
    // ponytail: static demo rates. Swap for a real FX feed if this ever needs to be live.
    private static readonly Dictionary<string, double> PerUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1.00, ["EUR"] = 0.92, ["GBP"] = 0.79,
        ["JPY"] = 157.0, ["CAD"] = 1.36, ["AUD"] = 1.51
    };

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "ISO currency code to convert from, e.g. USD" },
            "to":   { "type": "string", "description": "ISO currency code to convert to, e.g. EUR" }
          },
          "required": ["from", "to"]
        }
        """).RootElement;

    public string Name => "fx_rate";

    public string Description =>
        $"Returns the exchange rate (1 from = N to) for a currency pair. Supported: {string.Join(", ", PerUsd.Keys)}.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var from = call.Arguments.GetProperty("from").GetString() ?? "";
        var to = call.Arguments.GetProperty("to").GetString() ?? "";

        if (!PerUsd.TryGetValue(from, out var fromUsd) || !PerUsd.TryGetValue(to, out var toUsd))
            return Task.FromResult(new ToolResult(call.CallId,
                $"Unsupported currency. Supported codes: {string.Join(", ", PerUsd.Keys)}.", IsError: true));

        var rate = toUsd / fromUsd;
        return Task.FromResult(new ToolResult(call.CallId,
            $"1 {from.ToUpperInvariant()} = {rate.ToString("0.####", CultureInfo.InvariantCulture)} {to.ToUpperInvariant()}"));
    }
}
