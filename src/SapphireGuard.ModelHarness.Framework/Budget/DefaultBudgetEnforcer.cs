using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>
/// Naive budget enforcement: counts model-call turns, sums per-call cost, and
/// checks elapsed wall-clock. Token counting is left to the model client as
/// the loop has no tokeniser.
/// </summary>
public sealed class DefaultBudgetEnforcer : IBudgetEnforcer
{
    public BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt)
    {
        var budget = state.Budget;

        var turns = 0;
        var totalCost = state.SensorCost;
        var totalTokens = state.SensorUsage.TotalTokens;
        foreach (var step in state.Trajectory)
        {
            if (step is ModelCallStep call)
            {
                turns++;
                totalCost += call.Cost;
                totalTokens += call.Usage.TotalTokens;
            }
            else if (step is ToolCallStep toolStep)
            {
                if (toolStep.Result.Cost is { } delegatedCost)
                    totalCost += delegatedCost;
                if (toolStep.Result.Usage is { } delegatedUsage)
                    totalTokens += delegatedUsage.TotalTokens;
            }
        }

        if (turns >= budget.MaxTurns)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxTurns ({budget.MaxTurns}).");
        }
        if (totalCost >= budget.MaxCost)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxCost ({budget.MaxCost:F4}).");
        }
        if (totalTokens >= budget.MaxContextTokens)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxContextTokens ({budget.MaxContextTokens}).");
        }
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed >= budget.MaxWallClock)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxWallClock ({budget.MaxWallClock}).");
        }

        return BudgetCheckResult.Ok;
    }
}
