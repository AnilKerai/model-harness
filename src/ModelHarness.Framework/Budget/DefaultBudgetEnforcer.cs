using ModelHarness.Framework.State;

namespace ModelHarness.Framework.Budget;

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
        var totalCost = 0m;
        var totalTokens = 0;
        foreach (var step in state.Trajectory)
        {
            if (step is ModelCallStep call)
            {
                turns++;
                totalCost += call.Cost;
                totalTokens += call.Usage.TotalTokens;
            }
        }

        if (turns >= budget.MaxTurns)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxTurns ({budget.MaxTurns}).");
        }
        if (totalCost >= budget.MaxCostUsd)
        {
            return BudgetCheckResult.Exhausted($"Reached MaxCostUsd ({budget.MaxCostUsd:C}).");
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
