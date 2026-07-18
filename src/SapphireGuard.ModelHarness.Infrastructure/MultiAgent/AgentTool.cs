using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.MultiAgent;

/// <summary>
/// Exposes a named agent from <see cref="AgentFactory"/> as an <see cref="ITool"/>.
/// The orchestrating agent calls this tool with a sub-task; the tool runs the target
/// agent's full loop and returns its final answer as the tool result. An optional
/// <paramref name="budget"/> bounds each delegated run; when <see langword="null"/> the
/// sub-agent uses the agent's default budget.
/// </summary>
public sealed class AgentTool(string agentName, AgentFactory factory, Budget? budget = null) : ITool
{
    public string Name => agentName;
    public string Description =>
        $"Delegates a sub-task to the '{agentName}' agent. Pass a self-contained task description.";

    public JsonElement InputSchema => JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "task": { "type": "string", "description": "The sub-task to delegate." }
            },
            "required": ["task"]
        }
        """).RootElement;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var task = call.Arguments.GetProperty("task").GetString()
            ?? throw new InvalidOperationException("'task' argument is required.");

        var outcome = await factory.GetAgent(agentName).RunAsync(task, budget, ct: ct);

        // The framework's own accounting, not a model-call walk: a sub-agent that runs AI sensors,
        // compacts, or delegates onward spends where model-call steps can't see it, and reporting
        // low here silently under-charges the parent's budget — compounding with delegation depth.
        var (_, usage, cost) = outcome.FinalState.TotalSpend();

        return new ToolResult(
            call.CallId,
            outcome.FinalAnswer ?? outcome.FailureReason ?? "(no result)",
            IsError: outcome.Status == AgentStatus.Failed,
            Cost: cost > 0 ? cost : null,
            Usage: usage.TotalTokens > 0 ? usage : null);
    }
}
