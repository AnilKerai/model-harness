using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>
/// Per-turn budget enforcement for conversational agents. Counts model-call turns, cost, and
/// tokens incurred since the most recent <see cref="UserMessageStep"/>, so every user turn starts
/// with a fresh allowance. <see cref="DefaultBudgetEnforcer"/> sums the whole trajectory, which
/// exhausts a multi-turn chat after MaxTurns total model calls; this resets that window at each
/// user turn. Live context-window size stays bounded separately by the trajectory guide.
/// </summary>
public sealed class TurnScopedBudgetEnforcer(TimeProvider? timeProvider = null) : IBudgetEnforcer
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt)
    {
        var budget = state.Budget;

        var turns = 0;
        var totalCost = 0m;
        var totalTokens = 0;
        foreach (var step in state.Trajectory)
        {
            switch (step)
            {
                case UserMessageStep:
                    turns = 0;
                    totalCost = 0m;
                    totalTokens = 0;
                    break;
                case ModelCallStep call:
                    turns++;
                    totalCost += call.Cost;
                    totalTokens += call.Usage.TotalTokens;
                    break;
                case ToolCallStep toolStep:
                    if (toolStep.Result.Cost is { } delegatedCost)
                        totalCost += delegatedCost;
                    if (toolStep.Result.Usage is { } delegatedUsage)
                        totalTokens += delegatedUsage.TotalTokens;
                    break;
            }
        }

        // ponytail: sensor + compaction spend is whole-run cumulative on AgentState, not per-turn —
        // chat mode wires no AI sensors and defaults to the no-cost view compaction, so this is ~0.
        // Per-turn accounting would need per-step usage.
        totalCost += state.SensorCost + state.CompactionCost;
        totalTokens += state.SensorUsage.TotalTokens + state.CompactionUsage.TotalTokens;

        if (turns >= budget.MaxTurns)
            return BudgetCheckResult.Exhausted($"Reached MaxTurns ({budget.MaxTurns}) this turn.");
        if (totalCost >= budget.MaxCost)
            return BudgetCheckResult.Exhausted($"Reached MaxCost ({budget.MaxCost:F4}) this turn.");
        if (totalTokens >= budget.MaxTotalTokens)
            return BudgetCheckResult.Exhausted($"Reached MaxTotalTokens ({budget.MaxTotalTokens}) this turn.");

        var elapsed = _time.GetUtcNow() - startedAt;
        if (elapsed >= budget.MaxWallClock)
            return BudgetCheckResult.Exhausted($"Reached MaxWallClock ({budget.MaxWallClock}).");

        return BudgetCheckResult.Ok;
    }
}
