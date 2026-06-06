using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

public sealed class CheckCompanyNameMatchTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name_a": {
              "type": "string",
              "description": "The first company name to compare (e.g. from the internal client record)."
            },
            "name_b": {
              "type": "string",
              "description": "The second company name to compare (e.g. from Companies House or the company website)."
            }
          },
          "required": ["name_a", "name_b"]
        }
        """).RootElement;

    // Ordered longest-first so "limited" doesn't shadow "limited liability partnership".
    private static readonly string[] StripSuffixes =
    [
        "limited liability partnership", "llp", "limited", "ltd", "public limited company",
        "p.l.c.", "plc", "corporation", "incorporated", "inc", "corp", "group", "co", "llc", "lp"
    ];

    public string Name => "check_company_name_match";
    public string Description => "Deterministic fuzzy check: normalises both company names (strips legal suffixes and punctuation) then computes string similarity. Returns a structured verdict — use it directly.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var nameA = call.Arguments.TryGetProperty("name_a", out var a) ? a.GetString() ?? "" : "";
        var nameB = call.Arguments.TryGetProperty("name_b", out var b) ? b.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
            return CheckResult.Build(call.CallId, "Inconclusive", "Low", "One or both company names are empty.");

        var normA = Normalise(nameA);
        var normB = Normalise(nameB);

        if (normA.Equals(normB, StringComparison.OrdinalIgnoreCase))
            return CheckResult.Build(call.CallId, "Pass", "High",
                $"Names match exactly after normalisation ('{normA}').");

        if (normA.Contains(normB, StringComparison.OrdinalIgnoreCase) ||
            normB.Contains(normA, StringComparison.OrdinalIgnoreCase))
            return CheckResult.Build(call.CallId, "Pass", "Medium",
                $"Normalised name '{normA}' contains or is contained by '{normB}' — one appears to be a subset of the other.");

        var similarity = Similarity(normA, normB);
        var pct = (int)(similarity * 100);

        return similarity switch
        {
            >= 0.85 => CheckResult.Build(call.CallId, "Pass", "Medium",
                $"Names are highly similar after normalisation ('{normA}' vs '{normB}', {pct}% similarity)."),
            >= 0.60 => CheckResult.Build(call.CallId, "Inconclusive", "Medium",
                $"Names show partial similarity after normalisation ('{normA}' vs '{normB}', {pct}% similarity) — may be a trading name or subsidiary."),
            _ => CheckResult.Build(call.CallId, "Fail", "High",
                $"Names are materially different after normalisation ('{normA}' vs '{normB}', {pct}% similarity).")
        };
    }

    private static string Normalise(string name)
    {
        var n = name.ToLowerInvariant();
        n = n.Replace("&", "and");
        n = new string(n.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());

        foreach (var suffix in StripSuffixes)
        {
            var padded = " " + suffix;
            if (n.EndsWith(padded, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^padded.Length].TrimEnd();
                break;
            }
        }

        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        var distance = LevenshteinDistance(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var m = s.Length;
        var n = t.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = 0; i <= m; i++) dp[i, 0] = i;
        for (var j = 0; j <= n; j++) dp[0, j] = j;
        for (var i = 1; i <= m; i++)
            for (var j = 1; j <= n; j++)
                dp[i, j] = s[i - 1] == t[j - 1]
                    ? dp[i - 1, j - 1]
                    : 1 + Math.Min(dp[i - 1, j - 1], Math.Min(dp[i - 1, j], dp[i, j - 1]));
        return dp[m, n];
    }
}
