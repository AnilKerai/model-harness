using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Output;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public static class CostThrottle
{
    public static async Task RunAsync(string systemPrompt, Action<IServiceCollection> configure, CancellationToken ct = default)
    {
        AgentConsoleWriter.PrintHeader(
            "cost-throttle",
            "Soft spend cap set below the cost of one model call. CostThrottleSensor should fire before the second call.");

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configure(services);
        services.AddSingleton<ISensor>(_ => new CostThrottleSensor(softLimitUsd: 0.0005m));
        services.AddSingleton<ISensor, StuckDetector>();

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
