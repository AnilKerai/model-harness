using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>
/// Per-turn budget enforcement for conversational agents. Counts model-call turns, cost, and
/// tokens incurred since the most recent <see cref="UserMessageStep"/> — model calls, delegated tool
/// spend, and AI-sensor and compaction spend (measured against the baselines that step carries) — so
/// every user turn starts with a fresh allowance. <see cref="DefaultBudgetEnforcer"/> sums the whole trajectory, which
/// exhausts a multi-turn chat after MaxTurns total model calls; this resets that window at each
/// user turn. Live context-window size stays bounded separately by the trajectory guide.
/// </summary>
public sealed class TurnScopedBudgetEnforcer(TimeProvider? timeProvider = null) : IBudgetEnforcer
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public BudgetCheckResult Check(AgentState state, TimeSpan lookahead = default)
    {
        var budget = state.Budget;

        var turns = 0;
        var totalCost = 0m;
        var totalTokens = 0;
        UserMessageStep? turnStart = null;
        foreach (var step in state.Trajectory)
        {
            switch (step)
            {
                case UserMessageStep user:
                    turnStart = user;
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

        // Sensor + compaction spend is cumulative on AgentState, so charge only what accrued since
        // the latest user message's baseline (zero for the opening message and for legacy checkpoints).
        totalCost += state.SensorCost + state.CompactionCost
            - (turnStart?.SensorCostBaseline ?? 0m) - (turnStart?.CompactionCostBaseline ?? 0m);
        totalTokens += state.SensorUsage.TotalTokens + state.CompactionUsage.TotalTokens
            - (turnStart?.SensorUsageBaseline.TotalTokens ?? 0) - (turnStart?.CompactionUsageBaseline.TotalTokens ?? 0);

        if (turns >= budget.MaxTurns)
            return BudgetCheckResult.Exhausted($"Reached MaxTurns ({budget.MaxTurns}) this turn.");
        if (totalCost >= budget.MaxCost)
            return BudgetCheckResult.Exhausted($"Reached MaxCost ({budget.MaxCost:F4}) this turn.");
        if (totalTokens >= budget.MaxTotalTokens)
            return BudgetCheckResult.Exhausted($"Reached MaxTotalTokens ({budget.MaxTotalTokens}) this turn.");

        // Per-turn wall-clock: measured from the latest user message, so each turn gets a fresh
        // allowance — consistent with the turn/cost/token reset above and resume-safe (both are
        // derived from the trajectory, not a per-invocation capture).
        var anchor = turnStart?.Timestamp ?? _time.GetUtcNow();
        var elapsed = _time.GetUtcNow() - anchor + lookahead;
        if (elapsed >= budget.MaxWallClock)
            return BudgetCheckResult.Exhausted($"Reached MaxWallClock ({budget.MaxWallClock}).");

        return BudgetCheckResult.Ok;
    }
}
