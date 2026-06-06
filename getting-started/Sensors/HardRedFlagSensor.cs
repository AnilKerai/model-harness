using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Sensors;

/// <summary>
/// Domain-rule enforcer at PreReturn: if any of the three hard checks (domain match,
/// AP email, Companies House registration) show 🔴 Fail but no Jira ticket was raised
/// during the run, the agent is sent back to escalate before completing its report.
/// No LLM required — pure trajectory inspection.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class HardRedFlagSensor : ISensor
{
    public string Name => "hard-red-flag";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreReturn };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var text = state.Trajectory.OfType<ModelCallStep>().LastOrDefault()?.Response.Text;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(SensorResult.Pass);

        if (!HardRedFlagPresent(text))
            return Task.FromResult(SensorResult.Pass);

        var ticketRaised = state.Trajectory
            .OfType<ToolCallStep>()
            .Any(t => t.Call.ToolName == "create_jira_ticket" && !t.Result.IsError);

        if (ticketRaised)
            return Task.FromResult(SensorResult.Pass);

        return Task.FromResult(SensorResult.Intervene(
            "A hard red flag is present in checks 1–3 but no Jira ticket was raised. " +
            "Call create_jira_ticket to escalate this case to the credit control team before submitting your report."));
    }

    // Checks 1–3 (domain match, AP email, Companies House) are hard checks.
    // They are the first three data rows in the checks table.
    private static bool HardRedFlagPresent(string text)
    {
        var tables = ParseTables(text);
        if (tables.Count == 0)
            return false;

        var hardCheckRows = tables[0]
            .Skip(1)                          // skip header
            .Where(l => !l.Contains("---|")) // skip separator
            .Take(3)
            .ToList();

        return hardCheckRows.Any(row => row.Contains("🔴"));
    }

    private static List<List<string>> ParseTables(string text)
    {
        var tables = new List<List<string>>();
        List<string>? current = null;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith('|'))
            {
                current ??= [];
                current.Add(line.Trim());
            }
            else if (current is not null)
            {
                tables.Add(current);
                current = null;
            }
        }

        if (current is not null)
            tables.Add(current);

        return tables;
    }
}
