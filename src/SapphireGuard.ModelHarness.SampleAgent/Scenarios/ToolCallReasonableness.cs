using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Sensors;

using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.SampleAgent.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public static class ToolCallReasonableness
{
    public static async Task RunAsync(string systemPrompt, Action<IServiceCollection> configure, CancellationToken ct = default)
    {
        AgentConsoleWriter.PrintHeader(
            "tool-call-reasonableness",
            "Model is given a task that invites division by zero. ToolCallReasonablenessSensor should block the call before it dispatches.");

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configure(services);
        services.AddSingleton<ISensor, ToolCallReasonablenessSensor>();
        services.AddSingleton<ISensor, StuckDetector>();

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<HarnessLoop>()
            .RunAsync(AgentState.NewTask("What is 100 divided by 0?", Budget()), ct);

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
