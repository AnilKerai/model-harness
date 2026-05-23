using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Budget;

/// <summary>
/// Raised when a downstream collaborator (a tool, a sub-agent) violates the
/// agent's budget. The loop itself does not use exceptions for normal budget
/// flow; it consults <see cref="IBudgetEnforcer"/> and transitions gracefully.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class BudgetExceededException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
