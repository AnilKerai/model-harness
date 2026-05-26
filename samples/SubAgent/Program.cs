using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.MultiAgent;
using SapphireGuard.ModelHarness.Samples.Common;
using SapphireGuard.ModelHarness.Samples.SubAgent;

AgentConsoleWriter.PrintHeader(
    "sub-agent",
    "Orchestrator delegates research to a specialist agent. Fully scripted — no API key.");

var services = new ServiceCollection();

services.AddAgentFactory(factory =>
{
    factory.AddStandardAgent("research", builder => builder
        .WithSystemPrompt("You are a research specialist. Provide a concise factual summary on any topic.")
        .WithConsoleTracer()
        .WithModel(_ => new ResearchModelClient()));

    factory.AddStandardAgent("orchestrator", builder => builder
        .WithSystemPrompt("You are an orchestrator. Delegate research tasks to the research agent, then synthesise the findings into a final answer.")
        .WithConsoleTracer()
        .WithModel(_ => new OrchestratorModelClient())
        .AddSubAgentAsTool("research", factory));
});

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<AgentFactory>()
    .GetAgent("orchestrator")
    .RunAsync(
        "Research quantum computing and write a brief summary.",
        budget: new Budget
        {
            MaxTurns = 5,
            MaxContextTokens = 100_000,
            MaxCost = 1m,
            MaxWallClock = TimeSpan.FromSeconds(30)
        });

AgentConsoleWriter.PrintOutcome(outcome);
