namespace ModelHarness.Framework.State;

/// <summary>
/// The terminal result of a harness run. <see cref="Status"/> distinguishes
/// successful completion from partial results (budget-finalised) and failures.
/// </summary>
public sealed record AgentOutcome
{
    public required string TaskId { get; init; }
    public required AgentStatus Status { get; init; }
    public required string? FinalAnswer { get; init; }
    public required AgentState FinalState { get; init; }
    public string? FailureReason { get; init; }
}
