using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.BudgetEnforcer;
using SapphireGuard.ModelHarness.Samples.Common;

const int MaxTurns = 3;

AgentConsoleWriter.PrintHeader(
    "budget-enforcer",
    $"Agent loops forever — MaxTurns={MaxTurns} causes graceful PartialResult after {MaxTurns} model calls. Fully scripted — no API key.");

var services = new ServiceCollection();

services.AddModelHarness(builder => builder
    .WithSystemPrompt("You are a sample agent. Keep calling the echo tool until you are done.")
    .WithConsoleTracer()
    .WithToolRegistry<InMemoryToolRegistry>()
    .WithTool<EchoTool>()
    .WithModel(_ => new LoopingScriptedModelClient()));

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<Agent>().RunAsync(
    "Echo a message for each step you take.",
    budget: new Budget { MaxTurns = MaxTurns, MaxContextTokens = 100_000, MaxCostUsd = 1m, MaxWallClock = TimeSpan.FromSeconds(30) });

AgentConsoleWriter.PrintOutcome(outcome);
