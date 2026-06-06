using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace GettingStarted.Sensors;

/// <summary>
/// Structural guard at PreReturn: confirms the agent produced exactly two markdown
/// tables, that the checks table has exactly seven data rows, and that result values
/// in the first six rows are one of the three allowed emoji verdicts. No LLM required.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OutputFormatSensor : ISensor
{
    public string Name => "output-format";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreReturn };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var text = state.Trajectory.OfType<ModelCallStep>().LastOrDefault()?.Response.Text;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(SensorResult.Pass);

        var tables = ParseTables(text);

        if (tables.Count != 2)
            return Task.FromResult(SensorResult.Intervene(
                $"Output format error: found {tables.Count} markdown table(s) but exactly 2 are required. " +
                "You must produce BOTH tables with no prose or headings between them: " +
                "(1) the checks table (7 rows: the 6 verification checks plus the concerns row), " +
                "then immediately (2) the supporting links table with columns | Link type | URL | Notes |. " +
                "Do not omit, merge, or reorder them."));

        var dataRows = tables[0]
            .Skip(1)                          // skip header row
            .Where(l => !l.Contains("---|")) // skip separator row
            .ToList();

        if (dataRows.Count != 7)
            return Task.FromResult(SensorResult.Intervene(
                $"Output format error: the checks table must have exactly 7 data rows but found {dataRows.Count}. " +
                "Ensure all six verification checks plus the concerns row are present."));

        // Validate result column (index 2 after splitting on |) for the first six rows.
        // The seventh row (Concerns) intentionally has no emoji verdict.
        for (var i = 0; i < 6; i++)
        {
            var cols = dataRows[i].Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;
            var result = cols[1].Trim();
            if (!result.Contains("🟢") && !result.Contains("🔴") && !result.Contains("🟡"))
                return Task.FromResult(SensorResult.Intervene(
                    $"Output format error: row {i + 1} result value '{result}' is invalid. " +
                    "Each check row must use exactly one of 🟢 Pass, 🔴 Fail, or 🟡 Inconclusive."));
        }

        return Task.FromResult(SensorResult.Pass);
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
