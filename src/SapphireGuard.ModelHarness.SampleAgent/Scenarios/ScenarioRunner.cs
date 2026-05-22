using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public sealed class ScenarioRunner(
    string systemPrompt,
    Action<IServiceCollection> configureBase)
{
    private static readonly Budget DefaultBudget = new()
    {
        MaxTurns = 8,
        MaxContextTokens = 100_000,
        MaxCostUsd = 1.00m,
        MaxWallClock = TimeSpan.FromSeconds(60)
    };

    public async Task RunAsync(Scenario scenario, CancellationToken ct = default)
    {
        PrintHeader(scenario);

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configureBase(services);
        scenario.ConfigureSensors?.Invoke(services);

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<HarnessLoop>();

        var state = AgentState.NewTask(
            taskText: scenario.TaskText,
            budget: scenario.Budget ?? DefaultBudget);

        var outcome = await harness.RunAsync(state, ct);

        PrintOutcome(outcome);

        if (scenario.PostRun is not null)
            await scenario.PostRun(outcome, provider, ct);
    }

    private static void PrintHeader(Scenario scenario)
    {
        var bar = new string('─', 60);
        Console.WriteLine();
        Console.WriteLine(bar);
        Console.WriteLine($"  Scenario : {scenario.Name}");
        Console.WriteLine($"  {scenario.Description}");
        Console.WriteLine(bar);
    }

    private static void PrintOutcome(AgentOutcome outcome)
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
