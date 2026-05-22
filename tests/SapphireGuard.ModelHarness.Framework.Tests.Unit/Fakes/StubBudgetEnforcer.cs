using SapphireGuard.ModelHarness.Framework.State;
using BudgetNs = SapphireGuard.ModelHarness.Framework.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

public sealed class AlwaysOkBudgetEnforcer : BudgetNs.IBudgetEnforcer
{
    public BudgetNs.BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt) =>
        BudgetNs.BudgetCheckResult.Ok;
}

public sealed class AlwaysExhaustedBudgetEnforcer : BudgetNs.IBudgetEnforcer
{
    public BudgetNs.BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt) =>
        BudgetNs.BudgetCheckResult.Exhausted("test budget exhausted");
}
