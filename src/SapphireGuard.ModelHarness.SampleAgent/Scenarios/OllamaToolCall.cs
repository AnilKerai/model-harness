using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Ollama;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;

using SapphireGuard.ModelHarness.Infrastructure.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public static class OllamaToolCall
{
    public static async Task RunAsync(
        string systemPrompt,
        Action<IServiceCollection> configure,
        OllamaClientOptions ollamaOptions,
        CancellationToken ct = default)
    {
        AgentConsoleWriter.PrintHeader(
            "ollama-tool-call",
            "Runs a tool-calling task through a local Ollama model. Demonstrates ToolUse/Tool message grouping for Ollama's single-assistant-message format.");

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configure(services);
        services.AddOllamaModelClient(ollamaOptions);
        services.AddSingleton<ISensor, StuckDetector>();

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<HarnessLoop>()
            .RunAsync(AgentState.NewTask("What is 56 multiplied by 13?", Budget()), ct);

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
