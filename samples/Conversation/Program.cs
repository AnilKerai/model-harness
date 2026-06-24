using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Samples.Common;

AgentConsoleWriter.PrintHeader(
    "conversation",
    "Multi-turn conversation: a completed run is re-opened with WithUserMessage, so the " +
    "trajectory carries multiple user turns and turn 2 sees turn 1.");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder => builder
    .WithSystemPrompt("You are a friendly conversational assistant.")
    .WithModel(_ => new ConversationFakeClient()));

await using var provider = services.BuildServiceProvider();
var agent = provider.GetRequiredService<Agent>();

// ── Turn 1 ────────────────────────────────────────────────────────────────────

var first = await agent.RunAsync("Hi — what's your name?");

Console.WriteLine();
Console.WriteLine("── Turn 1 ──────────────────────────────────────────────────────────────");
AgentConsoleWriter.PrintOutcome(first);

// ── Turn 2 — continue the same conversation ────────────────────────────────────
// WithUserMessage appends the human's next turn and returns the run to Running.
// The carried-forward trajectory is what lets turn 2 see turn 1.

var second = await agent.RunAsync(first.FinalState.WithUserMessage("What did I just ask you?"));

Console.WriteLine();
Console.WriteLine("── Turn 2 ──────────────────────────────────────────────────────────────");
AgentConsoleWriter.PrintOutcome(second);

// ── Scripted fake: answers from the user turns it can see in the rendered prompt ─
// On turn 2 it sees both user messages, proving multiple user turns reach the model.

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
            ? "I'm Sapphire, your assistant. Ask me anything."
            : $"You first asked: \"{userTurns[0]}\". I can see all {userTurns.Count} of your messages this conversation.";

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
