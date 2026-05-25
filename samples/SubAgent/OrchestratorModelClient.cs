using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SubAgent;

/// <summary>
/// Scripted model client for the orchestrator agent. Two turns:
/// <list type="number">
///   <item>Turn 1 — delegate the research task to the research sub-agent.</item>
///   <item>Turn 2 — synthesise the sub-agent's findings into a final answer.</item>
/// </list>
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OrchestratorModelClient : IModelClient
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
            1 => DelegateTurn(),
            2 => SynthesisTurn(messages),
            _ => SafeFinal()
        });
    }

    private static ModelResponse DelegateTurn()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            task = "Provide a concise factual summary of quantum computing."
        });
        return new ModelResponse
        {
            Text = "I'll delegate the research to the specialist agent.",
            ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), "research", args)],
            StopReason = StopReason.ToolUse,
            Usage = new Usage(InputTokens: 100, OutputTokens: 20),
            Cost = 0m
        };
    }

    private static ModelResponse SynthesisTurn(IReadOnlyList<Message> messages)
    {
        var findings = messages.LastOrDefault(m => m.Role == MessageRole.Tool)?.Content
                       ?? "(no findings received)";
        return new ModelResponse
        {
            Text = $"Based on the research agent's findings: {findings}",
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = new Usage(InputTokens: 160, OutputTokens: 40),
            Cost = 0m
        };
    }

    private static ModelResponse SafeFinal() => new()
    {
        Text = "I have no further actions to take.",
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = new Usage(InputTokens: 50, OutputTokens: 10),
        Cost = 0m
    };
}
