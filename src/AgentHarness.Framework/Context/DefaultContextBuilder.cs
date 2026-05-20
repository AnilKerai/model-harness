using System.Text;
using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Context;

/// <summary>
/// Builds the next prompt as: system prompt + memory snippets + tool catalogue
/// + flattened trajectory (model turns, tool results, sensor interventions) +
/// the original task as a final user message.
/// </summary>
public sealed class DefaultContextBuilder(
    string systemPrompt,
    IToolSelector toolSelector,
    ITrajectoryCompactor compactor,
    IMemoryRetriever memoryRetriever) : IContextBuilder
{
    public async Task<IReadOnlyList<Message>> BuildAsync(
        AgentState state,
        IReadOnlyList<ITool> availableTools,
        CancellationToken ct)
    {
        var selectedTools = await toolSelector.SelectAsync(state, availableTools, ct).ConfigureAwait(false);
        var trajectory = await compactor.CompactAsync(state, ct).ConfigureAwait(false);
        var memories = await memoryRetriever.RetrieveAsync(state, ct).ConfigureAwait(false);

        var messages = new List<Message>
        {
            new(MessageRole.System, BuildSystem(systemPrompt, selectedTools, memories))
        };

        foreach (var step in trajectory)
        {
            switch (step)
            {
                case ModelCallStep mc:
                    if (!string.IsNullOrEmpty(mc.Response.Text))
                    {
                        messages.Add(new Message(MessageRole.Assistant, mc.Response.Text));
                    }
                    break;

                case ToolCallStep tc:
                    messages.Add(new Message(MessageRole.Assistant,
                        $"[tool_call name={tc.Call.ToolName} id={tc.Call.CallId}] {tc.Call.Arguments.GetRawText()}"));
                    messages.Add(new Message(MessageRole.Tool,
                        $"[tool_result id={tc.Result.CallId} error={tc.Result.IsError}] {tc.Result.Content}"));
                    break;

                case SensorInterventionStep si:
                    messages.Add(new Message(MessageRole.System,
                        $"[sensor:{si.SensorName} at {si.HookPoint}] {si.Reason} — adjust your plan accordingly."));
                    break;
            }
        }

        messages.Add(new Message(MessageRole.User, state.TaskText));
        return messages;
    }

    private static string BuildSystem(string systemPrompt, IReadOnlyList<ITool> tools, IReadOnlyList<string> memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine(systemPrompt);

        if (memories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Relevant memory");
            foreach (var m in memories)
            {
                sb.Append("- ").AppendLine(m);
            }
        }

        if (tools.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Available tools");
            foreach (var t in tools)
            {
                sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
            }
        }

        return sb.ToString();
    }
}
