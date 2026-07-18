namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// A point-in-time view of a run's cumulative resource usage against its <see cref="Budget"/>,
/// emitted once per turn via <see cref="Tracing.ITracer.LogBudgetSnapshot"/> so budget burn-down
/// is observable before exhaustion — not only at the terminal <c>PartialResult</c>. Usage is
/// cumulative across the whole run; note that chat-mode enforcement (TurnScopedBudgetEnforcer)
/// resets its allowance per user turn, so its enforcement window is narrower than this total.
/// </summary>
public sealed record BudgetSnapshot(
    int TurnsUsed, int MaxTurns,
    int TokensUsed, int MaxTotalTokens,
    decimal CostUsed, decimal MaxCost,
    TimeSpan Elapsed, TimeSpan MaxWallClock)
{
    /// <summary>Sums cumulative model/tool/sensor/compaction usage from <paramref name="state"/> against its budget.</summary>
    public static BudgetSnapshot From(AgentState state, TimeSpan elapsed)
    {
        var budget = state.Budget;
        var (turns, usage, cost) = state.TotalSpend();
        return new BudgetSnapshot(
            turns, budget.MaxTurns,
            usage.TotalTokens, budget.MaxTotalTokens,
            cost, budget.MaxCost,
            elapsed, budget.MaxWallClock);
    }
}
