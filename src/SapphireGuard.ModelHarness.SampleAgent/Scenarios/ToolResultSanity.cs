using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Output;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public static class ToolResultSanity
{
    public static async Task RunAsync(string systemPrompt, Action<IServiceCollection> configure, CancellationToken ct = default)
    {
        AgentConsoleWriter.PrintHeader(
            "tool-result-sanity",
            "A business-rule validator rejects calculator results above 1000. 124 × 37 = 4588, which exceeds the limit — ToolResultSanityCheckSensor should block.");

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configure(services);
        services.AddSingleton<ISensor, StuckDetector>();
        services.AddSingleton<ISensor>(_ => new ToolResultSanityCheckSensor(
            toolValidators: new Dictionary<string, Func<string, string?>>
            {
                ["calculator"] = result =>
                    double.TryParse(result, out var value) && value > 1000
                        ? $"result {value} exceeds the maximum allowed value of 1000"
                        : null
            }));

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<HarnessLoop>()
            .RunAsync(AgentState.NewTask("What is 124 multiplied by 37?", Budget()), ct);

        AgentConsoleWriter.PrintOutcome(outcome);
    }

    private static Budget Budget() => new()
    {
        MaxTurns = 8,
        MaxContextTokens = 100_000,
        MaxCostUsd = 1.00m,
        MaxWallClock = TimeSpan.FromSeconds(60)
    };
}
