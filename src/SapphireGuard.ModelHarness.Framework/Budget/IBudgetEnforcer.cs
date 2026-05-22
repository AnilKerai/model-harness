using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>Decides whether the agent has any remaining budget to take another turn.</summary>
public interface IBudgetEnforcer
{
    BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt);
}

/// <summary>Result of a budget evaluation. Block returns a human-readable reason.</summary>
public sealed record BudgetCheckResult(bool IsExhausted, string? Reason)
{
    public static BudgetCheckResult Ok { get; } = new(false, null);

    public static BudgetCheckResult Exhausted(string reason) => new(true, reason);
}
