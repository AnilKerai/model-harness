namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// The rolling summary of trajectory steps an incremental compaction strategy has folded out of
/// the live context. Persisted on <see cref="AgentState"/> and checkpointed, so a resumed run
/// rehydrates it verbatim and folds onward from <see cref="FoldedStepCount"/> rather than
/// re-summarising the whole evicted head from scratch.
/// </summary>
/// <param name="Text">The accumulated summary text, rendered into the prompt in place of the folded steps.</param>
/// <param name="FoldedStepCount">Number of rendered trajectory steps this summary represents — the watermark the trajectory guide folds onward from.</param>
public sealed record RollingSummary(string Text, int FoldedStepCount);
