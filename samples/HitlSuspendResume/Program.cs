using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;

AgentConsoleWriter.PrintHeader(
    "hitl-suspend-resume",
    "Demonstrates async HITL: the agent suspends at ask_human, the caller provides " +
    "the answer, and the run resumes — no blocking while waiting for input.");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder => builder
    .WithSystemPrompt("You are a helpful assistant. Ask the human for their preferred currency before answering.")
    .WithAskHumanTool<ConsoleHumanChannel>()
    .WithModel(_ => new HitlFakeClient()));

await using var provider = services.BuildServiceProvider();

var agent = provider.GetRequiredService<Agent>();
var budget = new Budget
{
    MaxTurns = 5,
    MaxContextTokens = 100_000,
    MaxCost = 1m,
    MaxWallClock = TimeSpan.FromMinutes(1)
};

// ── First run — suspends at ask_human ────────────────────────────────────────

var outcome = await agent.RunAsync("What is 100 in my preferred currency?", budget);

if (outcome.Status != AgentStatus.AwaitingHuman || outcome.PendingHumanInput is null)
{
    Console.WriteLine($"Unexpected status: {outcome.Status}");
    return;
}

Console.WriteLine();
Console.WriteLine($"Run suspended (status={outcome.Status})");
Console.WriteLine($"Pending call : {outcome.PendingHumanInput.CallId}");

// Read the answer that ConsoleHumanChannel already prompted for
var answer = Console.ReadLine() ?? string.Empty;

// ── Resume ────────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("── Resuming with human answer ──────────────────────────────────────────");

// FinalState.PendingHumanInput is the durable form — it is saved in the checkpoint so a
// process that crashes here and reloads from disk can resume without the AgentOutcome in memory.
var resumedState = outcome.FinalState.ResumeWithHumanAnswer(outcome.FinalState.PendingHumanInput!.CallId, answer);
var finalOutcome = await agent.RunAsync(resumedState);

AgentConsoleWriter.PrintOutcome(finalOutcome);

// ── Scripted fake: asks one question then uses the human's answer ─────────────

sealed class HitlFakeClient : IModelClient
{
    private int _turn;

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var turn = Interlocked.Increment(ref _turn);
        return Task.FromResult(turn switch
        {
            1 => AskHumanTurn(),
            2 => AnswerWithHumanInputTurn(messages),
            _ => new ModelResponse { Text = "I have no further actions.", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m }
        });
    }

    private static ModelResponse AskHumanTurn()
    {
        var args = JsonDocument.Parse("""{"question":"What is your preferred currency?"}""").RootElement;
        return new ModelResponse
        {
            Text = "I need some information from the operator.",
            ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), "ask_human", args)],
            StopReason = StopReason.ToolUse,
            Usage = Usage.Zero,
            Cost = 0m
        };
    }

    private static ModelResponse AnswerWithHumanInputTurn(IReadOnlyList<Message> messages)
    {
        var raw = messages.LastOrDefault(m => m.Role == MessageRole.Tool)?.Content ?? "(no answer)";
        var humanAnswer = ExtractToolContent(raw);
        return new ModelResponse
        {
            Text = $"The operator's preferred currency is: {humanAnswer}. I'll use that for all amounts.",
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = Usage.Zero,
            Cost = 0m
        };
    }

    // Tool result messages are rendered as "[tool_result id=... error=False] <content>".
    // Strip the prefix so the fake can work with the raw answer text.
    private static string ExtractToolContent(string raw)
    {
        var idx = raw.LastIndexOf(']');
        return idx >= 0 ? raw[(idx + 1)..].Trim() : raw.Trim();
    }
}
