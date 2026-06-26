using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Renders the agent's trajectory into <see cref="ContextDraft.TrajectoryMessages"/>,
/// trimming the oldest steps when the estimated token count approaches the context limit.
/// Each step type has a distinct rendering: model turns as assistant messages,
/// tool calls and results as paired messages, sensor interventions as assistant
/// acknowledgements so the model continues from a committed correction posture.
/// When steps are evicted, the injected <see cref="ICompactionStrategy"/> decides what
/// to put in their place — a bare omission note (<see cref="NullCompactionStrategy"/>)
/// or an AI-generated prose summary (<c>AiCompactionStrategy</c> in Infrastructure).
/// Must run last in the guide pipeline so it can measure all prior guide contributions
/// and compute an accurate token budget rather than relying on a fixed reserve.
/// When <paramref name="pinOriginalGoal"/> is <see langword="false"/> (chat mode) the
/// fixed <c>[ORIGINAL GOAL]</c> anchor is omitted — the latest user turn is already the
/// live goal, so re-pinning the first message would misdirect a multi-turn conversation.
/// </summary>
public sealed class HeadEvictionTrajectoryGuide(ICompactionStrategy compactionStrategy, bool pinOriginalGoal = true) : ITrajectoryGuide
{
    public string Name => "trajectory";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var stepGroups = RenderSteps(state.Trajectory);

        var budget = state.Budget.MaxContextTokens - EstimateDraftTokens(draft);
        var trimCount = ComputeTrimCount(stepGroups, budget);

        if (trimCount > 0)
        {
            var evictedContent = stepGroups[..trimCount]
                .SelectMany(g => g.Messages.Select(m => m.Content))
                .ToList();
            var summary = await compactionStrategy.SummariseAsync(trimCount, evictedContent, budget, ct);
            draft.TrajectoryMessages.Add(new Message(MessageRole.System, summary));
            stepGroups = stepGroups[trimCount..];
        }

        if (pinOriginalGoal)
            draft.TrajectoryMessages.Add(new Message(MessageRole.System,
                $"[ORIGINAL GOAL] {state.TaskText}"));

        foreach (var (_, messages) in stepGroups)
            draft.TrajectoryMessages.AddRange(messages);
    }

    private static List<(Step Step, List<Message> Messages)> RenderSteps(IReadOnlyList<Step> trajectory)
    {
        var groups = new List<(Step, List<Message>)>(trajectory.Count);

        for (var i = 0; i < trajectory.Count; i++)
        {
            var step = trajectory[i];
            var messages = new List<Message>();

            switch (step)
            {
                case UserMessageStep um:
                    messages.Add(new Message(MessageRole.User, um.Content));
                    break;

                case ModelCallStep mc:
                    // If a PostModelCall sensor intervened on this response, suppress the text —
                    // the model must not see its own flagged content on the next turn (e.g. PII).
                    // The SensorInterventionStep that follows tells the model why without repeating
                    // the content.
                    if (!string.IsNullOrEmpty(mc.Response.Text) && !IsIntervenedAtPostModelCall(trajectory, i))
                        messages.Add(new Message(MessageRole.Assistant, mc.Response.Text));
                    break;

                case ToolCallStep tc:
                    messages.Add(new Message(MessageRole.ToolUse,
                        $"[tool_call name={tc.Call.ToolName} id={tc.Call.CallId}] {tc.Call.Arguments.GetRawText()}"));
                    messages.Add(new Message(MessageRole.Tool,
                        $"[tool_result id={tc.Result.CallId} error={tc.Result.IsError}] {tc.Result.Content}"));
                    break;

                case SensorInterventionStep si:
                    messages.Add(new Message(MessageRole.Assistant,
                        $"[HARNESS OBSERVATION — {si.SensorName} at {si.HookPoint}] My previous response was blocked: {si.Reason} I will comply fully and not repeat this behaviour."));
                    break;
            }

            if (messages.Count > 0)
                groups.Add((step, messages));
        }

        return groups;
    }

    // Scans forward from a ModelCallStep to see if it was immediately blocked at PostModelCall.
    // Stops at the first non-intervention step (which would be the next model call or tool call).
    private static bool IsIntervenedAtPostModelCall(IReadOnlyList<Step> trajectory, int modelCallIndex)
    {
        for (var i = modelCallIndex + 1; i < trajectory.Count; i++)
        {
            if (trajectory[i] is SensorInterventionStep { HookPoint: HookPoint.PostModelCall })
                return true;
            if (trajectory[i] is not SensorInterventionStep)
                break;
        }
        return false;
    }

    // Sums the estimated token cost of everything already written to the draft by prior guides
    // (system prompt, memory snippets, system sections such as tool catalogue and skills).
    // TrajectoryMessages is empty at this point — HeadEvictionTrajectoryGuide hasn't written yet.
    private static int EstimateDraftTokens(ContextDraft draft)
    {
        var total = EstimateTokens(draft.SystemPrompt);
        total += draft.MemorySnippets.Sum(EstimateTokens);
        total += draft.SystemSections.Sum(EstimateTokens);
        return total;
    }

    private static int ComputeTrimCount(List<(Step Step, List<Message> Messages)> groups, int tokenBudget)
    {
        var total = groups.Sum(g => g.Messages.Sum(m => EstimateTokens(m.Content)));

        var trimCount = 0;
        while (trimCount < groups.Count && total > tokenBudget)
        {
            total -= groups[trimCount].Messages.Sum(m => EstimateTokens(m.Content));
            trimCount++;
        }

        return trimCount;
    }

    private static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
