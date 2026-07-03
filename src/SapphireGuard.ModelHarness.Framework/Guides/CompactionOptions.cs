namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Configuration for trajectory compaction — chiefly the trigger: how large the live context may
/// grow before <see cref="HeadEvictionTrajectoryGuide"/> evicts and compacts the oldest steps.
/// Distinct from <see cref="State.Budget"/>, which bounds the whole run (turns, cumulative tokens,
/// cost, wall clock). A default is registered for the base path; the compaction opt-in methods
/// (<c>WithAiCompaction</c>, <c>WithCompactionStrategy</c>) require it, so a strategy is never wired
/// without stating when it fires.
/// </summary>
public sealed record CompactionOptions
{
    /// <summary>
    /// The per-turn context-window token budget the trajectory guide keeps the rendered context
    /// under by evicting (and compacting) the oldest steps. This is a single-window size, not a
    /// cumulative run total (see <see cref="State.Budget.MaxTotalTokens"/> for that) — set it to
    /// your model's usable context window.
    /// </summary>
    public required int WindowTokens { get; init; }

    /// <summary>The registered default when no compaction options are supplied — a conservative 100k-token window.</summary>
    public static CompactionOptions Default { get; } = new() { WindowTokens = 100_000 };
}
