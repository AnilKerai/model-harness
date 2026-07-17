using System.Linq;
using System.Text;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Context;

/// <summary>
/// Runs the guide pipeline then assembles the populated <see cref="ContextDraft"/>
/// into a prompt: system message (prompt + memories + guide-rendered sections)
/// followed by the trajectory messages, which carry the conversation including
/// user turns rendered in position.
/// </summary>
public sealed class DefaultContextBuilder(IGuideRunner guideRunner) : IContextBuilder
{
    public async Task<ContextBuildResult> BuildAsync(
        AgentState state,
        IReadOnlyList<ITool> allTools,
        CancellationToken ct)
    {
        var draft = await guideRunner.RunAsync(state, allTools, ct);

        // Every model adapter forwards only the first System message and drops the rest, so any
        // System-role message left inline in the trajectory (the compaction summary and the
        // [ORIGINAL GOAL] anchor emitted by HeadEvictionTrajectoryGuide) would silently vanish.
        // Fold them into the single system message that survives; the conversation carries only
        // the non-System turns.
        var systemParts = new List<string> { AssembleSystemMessage(draft) };
        systemParts.AddRange(draft.TrajectoryMessages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content));

        var messages = new List<Message>
        {
            new(MessageRole.System, string.Join("\n\n", systemParts))
        };

        messages.AddRange(draft.TrajectoryMessages.Where(m => m.Role != MessageRole.System));

        return new ContextBuildResult(messages, draft.AvailableTools, draft.Compaction);
    }

    private static string AssembleSystemMessage(ContextDraft draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine(draft.SystemPrompt);

        if (draft.MemorySnippets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Relevant memory");
            foreach (var m in draft.MemorySnippets)
            {
                sb.Append("- ").AppendLine(m);
            }
        }

        foreach (var section in draft.SystemSections)
        {
            sb.AppendLine();
            sb.AppendLine(section);
        }

        return sb.ToString();
    }
}
