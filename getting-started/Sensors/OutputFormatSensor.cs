using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace GettingStarted.Sensors;

/// <summary>
/// Structural guard at PostModelCall: fires only on final (EndTurn) responses,
/// confirming the agent produced exactly two markdown tables, that the checks table
/// has exactly seven data rows, and that result values in the first six rows are one
/// of the three allowed emoji verdicts. No LLM required.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OutputFormatSensor : ISensor
{
    public string Name => "output-format";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreReturn };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        // Only check final answers — skip mid-task responses that include tool calls.
        var lastModel = state.Trajectory.OfType<ModelCallStep>().LastOrDefault();
        if (lastModel is null || lastModel.Response.ToolCalls.Count > 0)
            return Task.FromResult(SensorResult.Pass);

        var text = lastModel.Response.Text;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(SensorResult.Pass);

        var tables = ParseTables(text);

        if (tables.Count != 2)
            return Task.FromResult(SensorResult.Intervene(
                $"Your response contained {tables.Count} markdown table(s) but must contain exactly 2. " +
                "All evidence is already in your context from the tool calls earlier in this conversation — do not call any tools. " +
                "Respond with ONLY the two tables, nothing else: " +
                "(1) the checks table with all 7 rows, " +
                "then immediately (2) the supporting links table with columns | Link type | URL | Notes |."));

        var dataRows = tables[0]
            .Skip(1)                          // skip header row
            .Where(l => !l.Contains("---|")) // skip separator row
            .ToList();

        if (dataRows.Count != 7)
            return Task.FromResult(SensorResult.Intervene(
                $"The checks table must have exactly 7 data rows but found {dataRows.Count}. " +
                "Do not call any tools. Respond with ONLY the two tables using evidence already in your context: " +
                "all six verification check rows plus the concerns row, followed immediately by the supporting links table."));

        // Validate result column (index 2 after splitting on |) for the first six rows.
        // The seventh row (Concerns) intentionally has no emoji verdict.
        for (var i = 0; i < 6; i++)
        {
            var cols = dataRows[i].Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length < 2) continue;
            var result = cols[1].Trim();
            if (!result.Contains("🟢") && !result.Contains("🔴") && !result.Contains("🟡"))
                return Task.FromResult(SensorResult.Intervene(
                    $"Row {i + 1} result value '{result}' is invalid. " +
                    "Do not call any tools. Respond with ONLY the two tables: each check row must use exactly one of 🟢 Pass, 🔴 Fail, or 🟡 Inconclusive."));
        }

        return Task.FromResult(SensorResult.Pass);
    }

    private static List<List<string>> ParseTables(string text)
    {
        var tables = new List<List<string>>();
        List<string>? current = null;

        foreach (var line in text.Split('\n'))
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
