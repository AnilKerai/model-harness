namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Decides what to inject into the context in place of evicted trajectory steps.
/// The default <see cref="NullCompactionStrategy"/> inserts a bare omission note.
/// Replace with <c>AiCompactionStrategy</c> (in Infrastructure) to produce a
/// prose summary of the dropped segment via a lightweight model call.
/// </summary>
public interface ICompactionStrategy
{
    /// <summary>
    /// Produces a string that replaces <paramref name="evictedStepCount"/> evicted steps
    /// in the rendered context. <paramref name="evictedContent"/> contains the rendered
    /// message text of those steps (flattened), which an AI strategy can summarise.
    /// <paramref name="remainingTokenBudget"/> is the token headroom after the surviving
    /// steps are included — the strategy should stay well under this limit.
    /// Implementations must not throw; fall back to a bare omission note on any failure.
    /// </summary>
    Task<string> SummariseAsync(
        int evictedStepCount,
        IReadOnlyList<string> evictedContent,
        int remainingTokenBudget,
        CancellationToken ct);
}
