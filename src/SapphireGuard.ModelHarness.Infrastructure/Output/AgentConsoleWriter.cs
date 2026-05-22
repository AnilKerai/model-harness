using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Output;

[ExcludeFromCodeCoverage]
public static class AgentConsoleWriter
{
    public static void PrintHeader(string name, string description)
    {
        var bar = new string('─', 60);
        Console.WriteLine();
        Console.WriteLine(bar);
        Console.WriteLine($"  Scenario : {name}");
        Console.WriteLine($"  {description}");
        Console.WriteLine(bar);
    }

    public static void PrintOutcome(AgentOutcome outcome)
    {
        Console.WriteLine();
        Console.WriteLine($"Status      : {outcome.Status}");
        Console.WriteLine($"FinalAnswer : {outcome.FinalAnswer ?? "(none)"}");
        if (outcome.FailureReason is not null)
            Console.WriteLine($"Failure     : {outcome.FailureReason}");

        Console.WriteLine();
        Console.WriteLine("Trajectory:");
        foreach (var step in outcome.FinalState.Trajectory)
        {
            var line = step switch
            {
                ModelCallStep m =>
                    $"  [model]   stop={m.Response.StopReason,-10} " +
                    $"tools={m.Response.ToolCalls.Count} " +
                    $"cost=${m.Cost.ToString("F4", CultureInfo.InvariantCulture)}",
                ToolCallStep t =>
                    $"  [tool]    {t.Call.ToolName}({t.Call.Arguments.GetRawText()}) " +
                    $"→ {(t.Result.IsError ? "ERROR: " : "")}{t.Result.Content}",
                SensorInterventionStep s =>
                    $"  [HARNESS OBSERVATION — {s.SensorName} @ {s.HookPoint}] {s.Reason}",
                _ =>
                    $"  [?] {step.GetType().Name}"
            };
            Console.WriteLine(line);
        }
    }
}
