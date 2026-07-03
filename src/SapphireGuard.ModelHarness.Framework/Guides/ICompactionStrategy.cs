namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Decides what replaces the trajectory steps the harness evicts to stay within the context budget.
/// This is a framework extension point — implement it to plug in your own compaction (prose
/// summarisation, structured clearing, semantic compression) and register it via <c>WithAiCompaction</c>
/// or a custom DI registration. The built-in <see cref="NullCompactionStrategy"/> inserts a bare
/// omission note (a stateless view); <c>AiCompactionStrategy</c> (in Infrastructure) folds an
/// incremental prose summary.
/// </summary>
public interface ICompactionStrategy
{
    /// <summary>
    /// Produces the replacement for the steps being evicted this turn. Called in the loop's hot path,
    /// immediately before a model call, whenever the live trajectory exceeds the budget. Implementations
    /// should not throw — the harness wraps this call and falls back to an omission note — but must honour
    /// cancellation. Return <see cref="CompactionResult.UpdatedSummary"/> = <see langword="null"/> to
    /// behave as a stateless view, or a folded summary to compact incrementally (see
    /// <see cref="CompactionResult"/>).
    /// </summary>
    Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct);
}
