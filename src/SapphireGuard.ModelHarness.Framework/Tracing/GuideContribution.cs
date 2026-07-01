namespace SapphireGuard.ModelHarness.Framework.Tracing;

/// <summary>
/// The structural delta a single guide made to the context draft, captured by
/// <see cref="Guides.DefaultGuideRunner"/> and emitted through
/// <see cref="ITracer.LogGuideContribution"/>. Counts and tool names only — never prompt
/// bodies — so tracing the pipeline on every turn stays cheap. Surfaces the shaping
/// decisions that never reach the final prompt: which tools a selector dropped, whether
/// memory retrieval returned anything, how many trajectory messages eviction emitted.
/// </summary>
/// <param name="ToolsBefore">Tool count the guide saw on entry.</param>
/// <param name="ToolsAfter">Tool count the guide left behind.</param>
/// <param name="ToolsRemoved">Names of tools the guide filtered out.</param>
/// <param name="ToolsAdded">Names of tools the guide introduced.</param>
/// <param name="MemorySnippetsAdded">Memory snippets the guide appended (0 = retrieval surfaced nothing).</param>
/// <param name="SystemSectionsAdded">System-prompt sections the guide appended (e.g. tool or skills catalogue).</param>
/// <param name="TrajectoryMessagesAdded">Rendered trajectory messages the guide appended.</param>
/// <param name="SystemPromptCharDelta">Change in system-prompt length, in characters.</param>
public sealed record GuideContribution(
    int ToolsBefore,
    int ToolsAfter,
    IReadOnlyList<string> ToolsRemoved,
    IReadOnlyList<string> ToolsAdded,
    int MemorySnippetsAdded,
    int SystemSectionsAdded,
    int TrajectoryMessagesAdded,
    int SystemPromptCharDelta);
