namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// Hard limits on a run. The cumulative caps (turns, tokens, cost, wall clock) are checked at the top
/// of every loop turn; when one is exceeded the loop budget-finalises: one last model call with tools
/// disabled, then PartialResult. <see cref="MaxToolCallDuration"/> is the exception — a per-tool-call
/// deadline enforced during dispatch, surfaced to the model as a recoverable tool error rather than
/// triggering finalisation.
/// </summary>
public sealed record Budget
{
    /// <summary>Maximum number of loop turns before budget finalisation.</summary>
    public required int MaxTurns { get; init; }

    /// <summary>
    /// Maximum <em>cumulative</em> tokens across the whole run (all model, tool, sensor, and
    /// compaction calls) before budget finalisation. This is a run-total spend cap, not a
    /// context-window size — the per-turn window that drives eviction lives on
    /// <see cref="Guides.CompactionOptions.WindowTokens"/>.
    /// </summary>
    public required int MaxTotalTokens { get; init; }

    /// <summary>Maximum cumulative cost before budget finalisation.</summary>
    public required decimal MaxCost { get; init; }

    /// <summary>Maximum wall-clock duration from task start before budget finalisation.</summary>
    public required TimeSpan MaxWallClock { get; init; }

    /// <summary>
    /// Optional per-tool-call deadline. When set, each tool dispatch is cancelled after this duration and
    /// surfaced to the model as a recoverable tool error (the run continues — this does not trigger budget
    /// finalisation). Null (default) imposes no deadline. Bounds tools that honour the cancellation token;
    /// a tool that ignores it cannot be interrupted (.NET has no safe thread abort).
    /// </summary>
    public TimeSpan? MaxToolCallDuration { get; init; }
}
