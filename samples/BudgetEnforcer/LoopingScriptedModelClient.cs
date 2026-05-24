using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.BudgetEnforcer;

// Scripted client that never voluntarily stops: it keeps calling the echo tool
// on each turn so the budget enforcer is the only thing that ends the run.
// When the harness finalises on budget it passes no tools — the client detects
// this and returns a terminal answer instead.
public sealed class LoopingScriptedModelClient : IModelClient
{
    private int _step;

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var step = Interlocked.Increment(ref _step);

        if (availableTools.Count == 0)
            return Task.FromResult(Final(step));

        var args = JsonSerializer.SerializeToElement(new { message = $"step {step}" });
        return Task.FromResult(ToolTurn(step, args));
    }

    private static ModelResponse ToolTurn(int step, JsonElement args) => new()
    {
        Text = $"Still working — calling echo for step {step}.",
        ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), "echo", args)],
        StopReason = StopReason.ToolUse,
        Usage = new Usage(InputTokens: 80, OutputTokens: 15),
        Cost = 0m
    };

    private static ModelResponse Final(int step) => new()
    {
        Text = $"Budget limit reached after {step - 1} turns — here is my best partial answer.",
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = new Usage(InputTokens: 60, OutputTokens: 12),
        Cost = 0m
    };
}
