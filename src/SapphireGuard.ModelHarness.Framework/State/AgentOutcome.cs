namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// The terminal result of a harness run. <see cref="Status"/> distinguishes successful
/// completion from partial results (budget-finalised) and failures.
/// </summary>
public sealed record AgentOutcome
{
    /// <summary>Unique identifier for the task, matching <see cref="AgentState.TaskId"/>.</summary>
    public required string TaskId { get; init; }

    /// <summary>Terminal status of the run.</summary>
    public required AgentStatus Status { get; init; }

    /// <summary>The model's last text response. <see langword="null"/> if the run did not produce an answer.</summary>
    public required string? FinalAnswer { get; init; }

    /// <summary>The <see cref="AgentState"/> at the end of the run, including the full trajectory.</summary>
    public required AgentState FinalState { get; init; }

    /// <summary>Human-readable reason for a <see cref="AgentStatus.Failed"/> outcome. <see langword="null"/> otherwise.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Details of the pending human-input request when <see cref="Status"/> is
    /// <see cref="AgentStatus.AwaitingHuman"/>. <see langword="null"/> for all other statuses.
    /// Use <see cref="AgentState.ResumeWithHumanAnswer"/> with <see cref="FinalState"/> to continue the run.
    /// </summary>
    public PendingHumanInput? PendingHumanInput { get; init; }
}
