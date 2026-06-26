using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.MultiAgent;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.ChatSubAgent;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

AgentConsoleWriter.PrintHeader(
    "chat-sub-agent",
    "A turn-scoped chat agent (AddChatHarness) hosts a terminal-state currency-conversion " +
    "specialist (AddStandardModelHarness defaults) as a sub-agent tool. Ask the chat agent to " +
    "convert money and it delegates to the currency_converter agent, which uses fx_rate + the " +
    "calculator to do the work. The sub-agent's steps are traced to the console.");

var apiKey = config["Anthropic:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("\nThis sample drives two real agents through an interactive loop — it needs a model.");
    Console.WriteLine("Set Anthropic:ApiKey in samples/ChatSubAgent/appsettings.local.json and re-run.");
    return;
}

var modelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5";
ClaudeModelClient NewClient() => new(new ClaudeClientOptions { ApiKey = apiKey, ModelId = modelId });

// ── Domain agent: a standard (terminal-state) currency specialist with its own tools ──
await using var factory = new AgentFactory();
factory.AddStandardAgent("currency_converter", b => b
    .WithSystemPrompt(
        "You are a currency conversion specialist. To convert an amount, first call fx_rate to get " +
        "the exchange rate for the pair, then use the calculator to multiply the amount by the rate. " +
        "Reply with one clear sentence stating the converted amount, rounded sensibly.")
    .WithConsoleTracer()
    .WithModel(_ => NewClient())
    .WithTool<FxRateTool>()
    .WithTool<CalculatorTool>());

// ── Chat agent: turn-scoped, hosts the domain agent as a tool ──
var services = new ServiceCollection();
services.AddChatHarness(builder => builder
    .WithSystemPrompt(
        "You are a friendly general assistant. You cannot convert currencies yourself: whenever the " +
        "user asks to convert or compare money across currencies, delegate to the currency_converter " +
        "tool with a self-contained task (include the amount and both ISO currency codes), then relay " +
        "its answer in your own words. For anything else, just chat normally.")
    // Bare chat harness defaults to NullToolRegistry — a real registry is required to dispatch tools.
    .WithToolRegistry<InMemoryToolRegistry>()
    .WithResilientModel(_ => NewClient())
    .AddSubAgentAsTool("currency_converter", factory));

await using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<Agent>();

var budget = new Budget
{
    MaxTurns = 8,
    MaxContextTokens = 100_000,
    MaxCost = 1.00m,
    MaxWallClock = TimeSpan.FromMinutes(2)
};

Console.WriteLine("\nTry: \"convert 250 USD to EUR\" or \"how much is 1000 yen in pounds?\". Blank line / 'exit' to quit.\n");

AgentOutcome? outcome = null;
while (true)
{
    Console.Write("you>   ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Trim() is "exit" or "quit")
        break;

    var state = outcome is null
        ? AgentState.NewTask(input, budget)
        : outcome.FinalState.WithUserMessage(input);

    outcome = await agent.RunAsync(state);

    Console.WriteLine($"agent> {outcome.FinalAnswer ?? "(no answer)"}");
    if (outcome.Status != AgentStatus.Done)
        Console.WriteLine($"       [status: {outcome.Status}]");
    Console.WriteLine();
}

Console.WriteLine("Bye.");
