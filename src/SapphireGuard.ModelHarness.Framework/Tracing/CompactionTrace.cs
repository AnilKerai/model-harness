using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tracing;

/// <summary>
/// Structural record of a single compaction pass, emitted via <see cref="ITracer.LogCompaction"/>
/// when <c>HeadEvictionTrajectoryGuide</c> evicts and compacts. Surfaces what most needs watching on
/// a long run: how much context was reclaimed, whether it was folded or dropped, and what the fold cost.
/// </summary>
/// <param name="StepsEvicted">Number of trajectory step-groups evicted this pass.</param>
/// <param name="TokensReclaimed">Estimated tokens of the evicted slice removed from the rendered context.</param>
/// <param name="Folded"><see langword="true"/> if the strategy persisted an incremental summary (a fold); <see langword="false"/> for a stateless view (e.g. the omission note).</param>
/// <param name="Usage">Model token usage the compaction incurred (<see cref="Usage.Zero"/> for a deterministic strategy).</param>
/// <param name="Cost">Model cost the compaction incurred.</param>
public sealed record CompactionTrace(
    int StepsEvicted,
    int TokensReclaimed,
    bool Folded,
    Usage Usage,
    decimal Cost);
