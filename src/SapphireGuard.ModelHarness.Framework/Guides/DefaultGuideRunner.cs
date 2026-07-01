using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Runs supporting guides sequentially in registration order, then the trajectory guide last.
/// Sequential execution lets each guide build on contributions from the ones before it.
/// <see cref="ITrajectoryGuide"/> is always last so it can measure all prior contributions
/// and compute an accurate token budget — enforced by type, not by DI registration order.
/// After each guide runs, the structural delta it made to the draft is emitted as a
/// <see cref="GuideContribution"/> via <see cref="ITracer.LogGuideContribution"/>, so the
/// otherwise-invisible shaping decisions (tools filtered out, memory retrieved, steps evicted)
/// show up in traces alongside the model and tool calls they explain.
/// </summary>
public sealed class DefaultGuideRunner(
    IEnumerable<IGuide> guides,
    ITrajectoryGuide trajectoryGuide,
    ITracer? tracer = null) : IGuideRunner
{
    private readonly IReadOnlyList<IGuide> _guides = [.. guides];
    private readonly ITracer _tracer = tracer ?? new NullTracer();

    public async Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct)
    {
        var draft = new ContextDraft { AvailableTools = [.. allTools] };

        foreach (var guide in _guides)
        {
            ct.ThrowIfCancellationRequested();
            var before = Snapshot(draft);
            await guide.ContributeAsync(draft, state, ct);
            _tracer.LogGuideContribution(state.TaskId, guide.Name, Diff(before, draft));
        }

        ct.ThrowIfCancellationRequested();
        var trajectoryBefore = Snapshot(draft);
        await trajectoryGuide.ContributeAsync(draft, state, ct);
        _tracer.LogGuideContribution(state.TaskId, trajectoryGuide.Name, Diff(trajectoryBefore, draft));

        return draft;
    }

    private static DraftSnapshot Snapshot(ContextDraft draft) => new(
        [.. draft.AvailableTools.Select(t => t.Name)],
        draft.MemorySnippets.Count,
        draft.SystemSections.Count,
        draft.TrajectoryMessages.Count,
        draft.SystemPrompt.Length);

    private static GuideContribution Diff(DraftSnapshot before, ContextDraft after)
    {
        var afterTools = after.AvailableTools.Select(t => t.Name).ToList();
        var beforeSet = before.ToolNames.ToHashSet();
        var afterSet = afterTools.ToHashSet();
        return new GuideContribution(
            ToolsBefore: before.ToolNames.Count,
            ToolsAfter: afterTools.Count,
            ToolsRemoved: [.. before.ToolNames.Where(n => !afterSet.Contains(n))],
            ToolsAdded: [.. afterTools.Where(n => !beforeSet.Contains(n))],
            MemorySnippetsAdded: after.MemorySnippets.Count - before.MemorySnippets,
            SystemSectionsAdded: after.SystemSections.Count - before.SystemSections,
            TrajectoryMessagesAdded: after.TrajectoryMessages.Count - before.TrajectoryMessages,
            SystemPromptCharDelta: after.SystemPrompt.Length - before.SystemPromptLength);
    }

    private readonly record struct DraftSnapshot(
        IReadOnlyList<string> ToolNames,
        int MemorySnippets,
        int SystemSections,
        int TrajectoryMessages,
        int SystemPromptLength);
}
