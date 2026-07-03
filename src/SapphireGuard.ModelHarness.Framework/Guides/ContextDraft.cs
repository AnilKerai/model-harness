using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Mutable context under construction. Populated by <see cref="IGuide"/> instances
/// in registration order, then assembled into a final prompt by
/// <see cref="Context.IContextBuilder"/>.
/// </summary>
public sealed class ContextDraft
{
    /// <summary>The agent's system prompt. Set by <see cref="SystemPromptGuide"/>.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Tools available this turn. Initialised to the full registry list before
    /// guides run; a guide can filter or reorder to shape the model's action space.
    /// </summary>
    public List<ITool> AvailableTools { get; set; } = [];

    /// <summary>Memory snippets to surface in the prompt. Appended to by <see cref="MemoryGuide"/>.</summary>
    public List<string> MemorySnippets { get; } = [];

    /// <summary>
    /// Rendered trajectory messages. Populated by <see cref="HeadEvictionTrajectoryGuide"/>;
    /// any guide can append additional framing messages here.
    /// </summary>
    public List<Message> TrajectoryMessages { get; } = [];

    /// <summary>
    /// Pre-rendered system-prompt sections (e.g. tool catalogue, skills catalogue)
    /// appended after the system prompt and memory. Lets guides own their own
    /// rendering instead of the context builder hard-coding each section.
    /// </summary>
    public List<string> SystemSections { get; } = [];

    /// <summary>
    /// Set by <see cref="HeadEvictionTrajectoryGuide"/> when it compacts this turn: the compaction
    /// strategy's result, carrying the rolling summary to persist and any model spend to attribute.
    /// <see langword="null"/> when no compaction happened. The loop reads it to commit the summary
    /// and spend onto the next <see cref="State.AgentState"/> — the channel by which the guide's
    /// fold survives into subsequent turns.
    /// </summary>
    public CompactionResult? Compaction { get; set; }

    /// <summary>
    /// Set by <see cref="HeadEvictionTrajectoryGuide"/> when it compacts this turn: a trace of what was
    /// reclaimed and the fold's spend. <see langword="null"/> when no compaction happened. Emitted by
    /// <see cref="DefaultGuideRunner"/> via <see cref="ITracer.LogCompaction"/> — separate from
    /// <see cref="Compaction"/> (which the loop consumes to persist state).
    /// </summary>
    public CompactionTrace? CompactionTrace { get; set; }
}
