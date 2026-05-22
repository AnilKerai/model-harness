using System.Text;
using SapphireGuard.Framework.Guides;
using SapphireGuard.Framework.State;
using SapphireGuard.Framework.Tools;

namespace SapphireGuard.Framework.Context;

/// <summary>
/// Runs the guide pipeline then assembles the populated <see cref="ContextDraft"/>
/// into a prompt: system message (prompt + tool catalogue + memories),
/// trajectory messages, then the task as a final user message.
/// </summary>
public sealed class DefaultContextBuilder(IGuideRunner guideRunner) : IContextBuilder
{
    public async Task<ContextBuildResult> BuildAsync(
        AgentState state,
        IReadOnlyList<ITool> allTools,
        CancellationToken ct)
    {
        var draft = await guideRunner.RunAsync(state, allTools, ct);

        var messages = new List<Message>
        {
            new(MessageRole.System, AssembleSystemMessage(draft))
        };

        messages.AddRange(draft.TrajectoryMessages);
        messages.Add(new Message(MessageRole.User, state.TaskText));

        return new ContextBuildResult(messages, draft.AvailableTools);
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

        if (draft.AvailableTools.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Available tools");
            foreach (var t in draft.AvailableTools)
            {
                sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
            }
        }

        return sb.ToString();
    }
}
