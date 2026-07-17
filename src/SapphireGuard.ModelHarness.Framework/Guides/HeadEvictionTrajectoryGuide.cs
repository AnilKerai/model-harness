using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Renders the agent's trajectory into <see cref="ContextDraft.TrajectoryMessages"/>,
/// trimming the oldest steps when the estimated token count approaches the context limit.
/// Each step type has a distinct rendering: model turns as assistant messages,
/// tool calls and results as paired messages, sensor interventions as assistant
/// acknowledgements so the model continues from a committed correction posture.
/// When an intervention is the final turn, a trailing user turn is appended restating the
/// directive: newer Claude models reject a request that ends on an assistant turn (the retired
/// prefill behaviour), so the note keeps its self-consistency framing while a user turn carries
/// the required final role.
/// When steps are evicted, the injected <see cref="ICompactionStrategy"/> decides what
/// to put in their place — a bare omission note (<see cref="NullCompactionStrategy"/>)
/// or an AI-generated prose summary (<c>AiCompactionStrategy</c> in Infrastructure).
/// Steps already represented by <see cref="AgentState.RollingSummary"/> are skipped and only
/// the newly evicted slice is passed to the strategy; a folded result is handed back to the loop
/// via <see cref="ContextDraft.Compaction"/> to persist onto the next state, so an incremental
/// strategy never re-summarises the whole evicted head.
/// Must run last in the guide pipeline so it can measure all prior guide contributions
/// and compute an accurate token budget rather than relying on a fixed reserve.
/// When <paramref name="pinOriginalGoal"/> is <see langword="false"/> (chat mode) the
/// fixed <c>[ORIGINAL GOAL]</c> anchor is omitted — the latest user turn is already the
/// live goal, so re-pinning the first message would misdirect a multi-turn conversation.
/// </summary>
public sealed class HeadEvictionTrajectoryGuide(ICompactionStrategy compactionStrategy, CompactionOptions options, bool pinOriginalGoal = true) : ITrajectoryGuide
{
    public string Name => "trajectory";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var allGroups = RenderSteps(state.Trajectory);

        // Steps already represented by the rolling summary are not re-rendered — the fold watermark.
        // Head-only eviction keeps the trajectory head stable, so this group index is stable across turns.
        var foldedCount = Math.Min(state.RollingSummary?.FoldedStepCount ?? 0, allGroups.Count);
        var liveGroups = allGroups[foldedCount..];
        var priorSummary = state.RollingSummary;

        var budget = options.WindowTokens - EstimateDraftTokens(draft);
        if (priorSummary is not null)
            budget -= EstimateTokens(priorSummary.Text);

        // Never evict the most recent group: a fully-evicted turn renders zero conversational
        // messages, which the provider rejects as an empty `messages` array. Keeping one live group
        // may exceed the window when that single group is oversized, but an over-budget message
        // beats a hard 400.
        var trimCount = Math.Min(ComputeTrimCount(liveGroups, budget), Math.Max(0, liveGroups.Count - 1));

        // Default: carry the prior summary forward unchanged (nothing new evicted this turn).
        var summaryToRender = priorSummary?.Text;

        if (trimCount > 0)
        {
            var evicted = liveGroups[..trimCount];
            var evictedSteps = evicted.Select(g => g.Step).ToList();
            var tokensReclaimed = evicted.Sum(g => g.Messages.Sum(m => EstimateTokens(m.Content)));
            CompactionResult result;
            try
            {
                result = await compactionStrategy.CompactAsync(new CompactionRequest
                {
                    State = state,
                    EvictedSteps = evictedSteps,
                    PriorSummary = priorSummary,
                    RemainingTokenBudget = budget
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A third-party strategy must never take down the run — fall back to a bare note.
                result = CompactionResult.OmissionNote(trimCount);
            }

            summaryToRender = result.InjectedText;
            draft.Compaction = result;
            draft.CompactionTrace = new CompactionTrace(
                StepsEvicted: trimCount,
                TokensReclaimed: tokensReclaimed,
                Folded: result.UpdatedSummary is not null,
                Usage: result.Usage,
                Cost: result.Cost);
            liveGroups = liveGroups[trimCount..];
        }

        // Defensive: if the fold watermark already covers every group (e.g. resuming a checkpoint
        // written before the keep-last-group rule), render the most recent group anyway so the
        // conversation is never empty. It is also represented in the summary — harmless overlap.
        if (liveGroups.Count == 0 && allGroups.Count > 0)
            liveGroups = [allGroups[^1]];

        if (!string.IsNullOrEmpty(summaryToRender))
            draft.TrajectoryMessages.Add(new Message(MessageRole.System, summaryToRender));

        if (pinOriginalGoal)
            draft.TrajectoryMessages.Add(new Message(MessageRole.System,
                $"[ORIGINAL GOAL] {state.TaskText}"));

        foreach (var (_, messages) in liveGroups)
            draft.TrajectoryMessages.AddRange(messages);

        // When an intervention note is the final turn, the request would end on an assistant turn.
        // Newer Claude models reject that (the retired prefill behaviour) with a 400. Keep the note
        // assistant-role — the model still "owns" the correction (self-consistency) — and append a
        // trailing user turn that both satisfies the required final role and restates the directive,
        // so the external command and the self-commitment reinforce each other.
        if (liveGroups.Count > 0 && liveGroups[^1].Step is SensorInterventionStep)
            draft.TrajectoryMessages.Add(new Message(MessageRole.User,
                "Proceed, fully complying with the harness observation above."));
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
                {
                    // Advisory hookpoints (PreModelCall guidance, PostToolCall flag) block nothing,
                    // so the acknowledgement must not claim the response was blocked.
                    var acknowledgement = si.HookPoint is HookPoint.PreModelCall or HookPoint.PostToolCall
                        ? $"{si.Reason} I will follow this guidance."
                        : $"My previous response was blocked: {si.Reason} I will comply fully and not repeat this behaviour.";
                    messages.Add(new Message(MessageRole.Assistant,
                        $"[HARNESS OBSERVATION — {si.SensorName} at {si.HookPoint}] {acknowledgement}"));
                    break;
                }
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
