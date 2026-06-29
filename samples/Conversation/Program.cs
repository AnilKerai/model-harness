using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

AgentConsoleWriter.PrintHeader(
    "conversation",
    "Interactive multi-turn chat via AddChatHarness. Each message is a new turn carried forward " +
    "with WithUserMessage, so the model sees the whole conversation. TurnScopedBudgetEnforcer gives " +
    "every turn a fresh per-turn budget, so the chat never exhausts a single run's allowance.");

if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using a scripted fake client.");

var services = new ServiceCollection();

services.AddChatHarness(builder =>
{
    builder.WithSystemPrompt("You are a friendly conversational assistant. Keep replies to one sentence.");

    if (usingRealModel)
        builder.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = apiKey!,
            ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5"
        }));
    else
        builder.WithModel(_ => new ConversationFakeClient());
});

await using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<Agent>();
var timeProvider = provider.GetRequiredService<TimeProvider>();

// Per-turn allowance — reset at each user turn by TurnScopedBudgetEnforcer.
var budget = new Budget
{
    MaxTurns = 8,
    MaxContextTokens = 100_000,
    MaxCost = 1.00m,
    MaxWallClock = TimeSpan.FromMinutes(2)
};

Console.WriteLine("Type a message and press Enter. Blank line, 'exit', or Ctrl-D to quit.\n");

AgentOutcome? outcome = null;
while (true)
{
    Console.Write("you>   ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Trim() is "exit" or "quit")
        break;

    var now = timeProvider.GetUtcNow();
    var state = outcome is null
        ? AgentState.NewTask(input, budget, now)
        : outcome.FinalState.WithUserMessage(input, now);

    outcome = await agent.RunAsync(state);

    Console.WriteLine($"agent> {outcome.FinalAnswer ?? "(no answer)"}");
    if (outcome.Status != AgentStatus.Done)
        Console.WriteLine($"       [status: {outcome.Status}]");
    Console.WriteLine();
}

Console.WriteLine("Bye.");

// Scripted fake used when no API key is set: proves each turn sees the full prior conversation.
sealed class ConversationFakeClient : IModelClient
{
    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var userTurns = messages
            .Where(m => m.Role == MessageRole.User)
            .Select(m => m.Content)
            .ToList();

        var answer = userTurns.Count <= 1
            ? "I'm Sapphire, your assistant."
            : $"You've sent me {userTurns.Count} messages; the first was \"{userTurns[0]}\".";

        return Task.FromResult(new ModelResponse
        {
            Text = answer,
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = Usage.Zero,
            Cost = 0m
        });
    }
}
