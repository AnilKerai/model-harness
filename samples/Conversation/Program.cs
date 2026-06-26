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
    "Multi-turn chat via AddChatHarness. A deliberately small per-turn budget (MaxTurns=3) is " +
    "reused across 6 turns: TurnScopedBudgetEnforcer resets the allowance each user turn, so " +
    "every turn finishes Done. Under the terminal-mode DefaultBudgetEnforcer the same budget would " +
    "PartialResult from turn 4 onward, since it sums model calls across the whole conversation.");

if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using a scripted fake client.\n");

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

// Per-turn budget that is smaller than the number of conversation turns — the whole point.
var budget = new Budget
{
    MaxTurns = 3,
    MaxContextTokens = 100_000,
    MaxCost = 1.00m,
    MaxWallClock = TimeSpan.FromMinutes(2)
};

var messages = new[]
{
    "Hi — what's your name?",
    "What did I just ask you?",
    "Name three primary colours.",
    "Which of those three is the warmest?",
    "Summarise what we've talked about so far.",
    "Thanks — goodbye!"
};

AgentOutcome? outcome = null;
for (var i = 0; i < messages.Length; i++)
{
    var state = outcome is null
        ? AgentState.NewTask(messages[i], budget)
        : outcome.FinalState.WithUserMessage(messages[i]);

    outcome = await agent.RunAsync(state);

    Console.WriteLine($"── Turn {i + 1} [{outcome.Status}] ──────────────────────────────────────");
    Console.WriteLine($"  you:   {messages[i]}");
    Console.WriteLine($"  agent: {outcome.FinalAnswer}");
    Console.WriteLine();
}

var lastTurnDone = outcome is { Status: AgentStatus.Done };
Console.WriteLine(lastTurnDone
    ? "Last turn is Done — per-turn budget held across a conversation longer than MaxTurns."
    : "NOTE: the final turn was not Done — check the statuses above.");

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
