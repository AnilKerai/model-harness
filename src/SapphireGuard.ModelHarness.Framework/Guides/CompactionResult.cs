using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// What a <see cref="ICompactionStrategy"/> produces for one compaction pass. <see cref="InjectedText"/>
/// is the complete replacement rendered in place of the evicted steps this turn; the optional
/// <see cref="UpdatedSummary"/> decides whether the strategy behaves as a stateless view or an
/// incremental fold.
/// </summary>
public sealed record CompactionResult
{
    /// <summary>
    /// The complete text rendered into the prompt in place of the evicted steps. It must stand on its
    /// own — a fold returns the full folded summary here, not just the delta. An empty string injects
    /// nothing (e.g. a strategy that only drops steps deterministically).
    /// </summary>
    public required string InjectedText { get; init; }

    /// <summary>
    /// The updated rolling summary to persist onto <see cref="AgentState"/> for the next turn.
    /// <see langword="null"/> makes the strategy a stateless <em>view</em>: nothing is persisted and
    /// the whole evicted head is reconsidered next turn (the <see cref="NullCompactionStrategy"/>
    /// default). A non-null value makes it an incremental <em>fold</em>: only newly evicted steps are
    /// summarised next turn, so summarisation cost stays flat as the run grows.
    /// </summary>
    public RollingSummary? UpdatedSummary { get; init; }

    /// <summary>Model token usage the strategy incurred, if any — attributed to the run's budget. Zero for deterministic strategies.</summary>
    public Usage Usage { get; init; } = Usage.Zero;

    /// <summary>Model cost the strategy incurred, if any — attributed to the run's budget. Zero for deterministic strategies.</summary>
    public decimal Cost { get; init; } = 0m;

    /// <summary>
    /// The standard bare omission note — a stateless view over <paramref name="evictedStepCount"/>
    /// dropped steps. Used by <see cref="NullCompactionStrategy"/> and as the harness's fail-open
    /// fallback when a strategy throws.
    /// </summary>
    public static CompactionResult OmissionNote(int evictedStepCount) =>
        new() { InjectedText = $"[{evictedStepCount} earlier step(s) omitted — context window limit]" };
}
