using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace GettingStarted.Sensors;

/// <summary>
/// AI-powered guard at PreReturn: cross-references each 🟢 Pass verdict in the
/// checks table against the tool call results in the trajectory. If a Pass is not
/// supported by the evidence gathered, the agent is challenged to revise its report.
/// Uses the same model client as the agent itself.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class EvidenceGroundingSensor(IModelClient modelClient) : ISensor
{
    private const int MaxToolSummaryChars = 20_000;
    private const int MaxPerResultChars   =  3_000;

    private static readonly Message SystemMessage = new(MessageRole.System,
        """
        You are an evidence auditor for a credit control verification system.
        You will be given tool call results gathered during a debtor verification, and the agent's final verification table.
        Your job is to check whether each 🟢 Pass verdict is actually supported by the evidence.

        Some results come from deterministic check tools (check_email_domain_match, check_ap_email_pattern,
        check_company_name_match, check_phone_format). These are authoritative — a Pass returned by one of
        these tools is sufficient evidence for that check. Do not challenge verdicts that are directly backed
        by a matching deterministic tool result.

        For checks that rely on web or database evidence (web_search, web_fetch, fetch_query_results),
        verify that the Pass verdict is directly supported by the content of those results.
        Only use the evidence provided — do not use any prior knowledge about the company.

        Reply with a JSON object only — no other text: {"grounded": true, "issues": []}
        grounded=true  → all Pass verdicts are supported by the evidence.
        grounded=false → at least one Pass verdict is not supported or is contradicted by the evidence.
        issues → a short list of specific concerns (empty if grounded=true).
        """);

    public string Name => "evidence-grounding";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreReturn };

    public async Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var finalText = state.Trajectory.OfType<ModelCallStep>().LastOrDefault()?.Response.Text;
        if (string.IsNullOrWhiteSpace(finalText))
            return SensorResult.Pass;

        var toolSummary = BuildToolSummary(state);
        if (string.IsNullOrWhiteSpace(toolSummary))
            return SensorResult.Pass;

        var userPrompt =
            $"""
            === Evidence gathered (tool call results) ===
            {toolSummary}

            === Agent's final verification table ===
            {finalText}

            For each check row showing 🟢 Pass, confirm it is directly supported by the evidence above.
            """;

        try
        {
            var response = await modelClient.CallAsync(
                [SystemMessage, new Message(MessageRole.User, userPrompt)],
                [],
                ct);

            if (response.Text is null)
                return SensorResult.Pass;

            var start = response.Text.IndexOf('{');
            var end   = response.Text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return SensorResult.Pass;

            using var json = JsonDocument.Parse(response.Text[start..(end + 1)]);
            var grounded = json.RootElement.GetProperty("grounded").GetBoolean();

            if (grounded)
                return SensorResult.Pass;

            var issues = json.RootElement.TryGetProperty("issues", out var issuesEl)
                ? string.Join("; ", issuesEl.EnumerateArray().Select(i => i.GetString() ?? ""))
                : "unspecified grounding issues";

            return SensorResult.InterveneWithToolSuppression(
                $"Evidence grounding check failed: {issues} " +
                "Respond with ONLY the two tables. For any check where the evidence does not directly support a 🟢 Pass verdict, change it to 🟡 Inconclusive.");
        }
        catch
        {
            return SensorResult.Pass;
        }
    }

    private static string BuildToolSummary(AgentState state)
    {
        var parts = state.Trajectory
            .OfType<ToolCallStep>()
            .Where(t => !t.Result.IsError && t.Call.ToolName is
                "web_search" or "web_fetch" or "fetch_query_results" or
                "check_email_domain_match" or "check_ap_email_pattern" or
                "check_company_name_match" or "check_phone_format")
            .Select(t =>
            {
                var content = t.Result.Content;
                if (content.Length > MaxPerResultChars)
                    content = content[..MaxPerResultChars] + "[truncated]";
                return $"[{t.Call.ToolName}]\n{content}";
            });

        var summary = string.Join("\n\n---\n\n", parts);

        return summary.Length > MaxToolSummaryChars
            ? summary[..MaxToolSummaryChars] + "\n[truncated]"
            : summary;
    }
}
