using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Guides;

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
    /// Rendered trajectory messages. Populated by <see cref="TrajectoryGuide"/>;
    /// any guide can append additional framing messages here.
    /// </summary>
    public List<Message> TrajectoryMessages { get; } = [];
}
