using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>
/// Naive budget enforcement: counts model-call turns, sums per-call cost, and
/// checks elapsed wall-clock. Token counting is left to the model client as
/// the loop has no tokeniser. Wall-clock is measured from the run's first user message
/// (<see cref="AgentState.RunStartedAt"/>), so it accumulates across a resume instead of
/// restarting each time the loop is re-entered.
/// </summary>
public sealed class DefaultBudgetEnforcer(TimeProvider? timeProvider = null) : IBudgetEnforcer
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public BudgetCheckResult Check(AgentState state, TimeSpan lookahead = default)
    {
        var budget = state.Budget;
        var elapsed = _time.GetUtcNow() - (state.RunStartedAt ?? _time.GetUtcNow()) + lookahead;
        // Accounting is shared with the per-turn telemetry snapshot so the two never drift.
        var s = BudgetSnapshot.From(state, elapsed);

        if (s.TurnsUsed >= budget.MaxTurns)
            return BudgetCheckResult.Exhausted($"Reached MaxTurns ({budget.MaxTurns}).");
        if (s.CostUsed >= budget.MaxCost)
            return BudgetCheckResult.Exhausted($"Reached MaxCost ({budget.MaxCost:F4}).");
        if (s.TokensUsed >= budget.MaxTotalTokens)
            return BudgetCheckResult.Exhausted($"Reached MaxTotalTokens ({budget.MaxTotalTokens}).");
        if (s.Elapsed >= budget.MaxWallClock)
            return BudgetCheckResult.Exhausted($"Reached MaxWallClock ({budget.MaxWallClock}).");

        return BudgetCheckResult.Ok;
    }
}
