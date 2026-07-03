using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Model;

/// <summary>
/// Scripted model client used by the walking skeleton. Counts how many times
/// it has been called and emits a pre-canned response per turn:
/// <list type="number">
///   <item>Turn 1: issue a <c>calculator</c> tool call (12 * 7).</item>
///   <item>Turn 2: return a final natural-language answer using the tool result.</item>
///   <item>Subsequent turns: return a terminal apology — keeps the loop bounded if mis-driven.</item>
/// </list>
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FakeModelClient : IModelClient
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
            1 => ToolCallTurn(),
            2 => FinalAnswerTurn(messages),
            _ => SafeFinal()
        });
    }

    private static ModelResponse ToolCallTurn()
    {
        var args = JsonDocument.Parse("""{"op":"mul","lhs":12,"rhs":7}""").RootElement;
        return new ModelResponse
        {
            Text = "I'll use the calculator to compute 12 × 7.",
            ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), "calculator", args)],
            StopReason = StopReason.ToolUse,
            Usage = new Usage(InputTokens: 120, OutputTokens: 25),
            Cost = 0.0012m,
            Model = "fake-model",
            Provider = "fake"
        };
    }

    private static ModelResponse FinalAnswerTurn(IReadOnlyList<Message> messages)
    {
        var toolReply = messages.LastOrDefault(m => m.Role == MessageRole.Tool)?.Content ?? "(no tool result)";
        return new ModelResponse
        {
            Text = $"12 × 7 = {ExtractNumber(toolReply)}.",
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = new Usage(InputTokens: 160, OutputTokens: 14),
            Cost = 0.0009m,
            Model = "fake-model",
            Provider = "fake"
        };
    }

    private static ModelResponse SafeFinal() => new()
    {
        Text = "I have no further actions to take.",
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = new Usage(InputTokens: 50, OutputTokens: 10),
        Cost = 0.0003m,
        Model = "fake-model",
        Provider = "fake"
    };

    private static string ExtractNumber(string toolReply)
    {
        var idx = toolReply.LastIndexOf(']');
        return (idx >= 0 ? toolReply[(idx + 1)..] : toolReply).Trim();
    }
}
