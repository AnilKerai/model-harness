using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>Decides whether the agent has any remaining budget to take another turn.</summary>
public interface IBudgetEnforcer
{
    /// <summary>
    /// Evaluates the run's budget. The wall-clock start is derived from the trajectory (so it
    /// survives resume), not passed in. <paramref name="lookahead"/> is added to the measured
    /// elapsed time before the wall-clock comparison, letting the loop ask "would waiting this
    /// long exhaust the budget?" before it sleeps for a rate-limit backoff (default: no lookahead).
    /// </summary>
    BudgetCheckResult Check(AgentState state, TimeSpan lookahead = default);
}

/// <summary>Result of a budget evaluation. Block returns a human-readable reason.</summary>
public sealed record BudgetCheckResult(bool IsExhausted, string? Reason)
{
    public static BudgetCheckResult Ok { get; } = new(false, null);

    public static BudgetCheckResult Exhausted(string reason) => new(true, reason);
}
