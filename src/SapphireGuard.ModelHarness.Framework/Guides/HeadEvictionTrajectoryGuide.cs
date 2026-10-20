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
/// the required final role. That turn demands compliance only when a blocking note is among the
/// trailing ones; advisory notes (<see cref="HookPoint.PreModelCall"/>, <see cref="HookPoint.PostToolCall"/>)
/// ask only that the model take them into account.
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
/// Nothing protects that turn from eviction, though: when the current turn's tool results alone
/// overflow the window, its group is trimmed (or sits behind the fold watermark) and the model
/// would no longer know what was asked. So in that case the latest user message is rendered
/// verbatim as a <c>[CURRENT REQUEST]</c> system note instead; while it survives in position,
/// nothing extra is rendered.
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

        // Everything rendered into the window must be charged to the budget before trimming against it.
        // The summary and the [ORIGINAL GOAL] anchor are appended *after* the trim but ride in the same
        // window, so omitting them overshoots WindowTokens by their size on every compacted turn. This
        // was inert while the adapters dropped all but the first System message; folding those into the
        // system message — so they actually reach the model — made the overshoot real. The prior
        // summary's size stands in for the new one: the strategy is handed the remaining budget below
        // and is expected to fit it.
        var budget = options.WindowTokens - EstimateDraftTokens(draft);
        if (priorSummary is not null)
            budget -= EstimateTokens(priorSummary.Text);
        if (pinOriginalGoal)
            budget -= EstimateTokens(OriginalGoal(state.TaskText));

        // Chat mode: the latest user turn is the live goal and has no pin, so it may be evicted and
        // have to be re-rendered as a system note. Reserve room for that note up front rather than
        // trimming twice; the reserve is conservative, as the goal anchor's is, since the note is
        // only emitted when the turn is actually gone.
        var currentRequest = pinOriginalGoal ? null : state.Trajectory.OfType<UserMessageStep>().LastOrDefault();
        if (currentRequest is not null)
            budget -= EstimateTokens(CurrentRequest(currentRequest.Content));

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
            // Only genuine harness cancellation propagates. A strategy's *own* OperationCanceledException
            // — an HttpClient timeout surfaces as TaskCanceledException even though our token is
            // untouched — must fall through to the fail-open catch below, or one slow compaction
            // model ends the run as "cancelled".
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A third-party strategy must never take down the run — fall back to a bare note,
                // and carry the reason so the failure is visible in telemetry rather than silent.
                result = CompactionResult.OmissionNote(trimCount) with { Error = $"{ex.GetType().Name}: {ex.Message}" };
            }

            // A result that persists nothing — a view, or a fallback — describes only the newly
            // evicted slice. Steps behind the fold watermark are no longer rendered, so a prior
            // summary must stay in front of it: it is the only trace of them left in context.
            summaryToRender = result.UpdatedSummary is null && priorSummary is not null
                ? string.Join("\n", new[] { priorSummary.Text, result.InjectedText }.Where(t => !string.IsNullOrEmpty(t)))
                : result.InjectedText;
            draft.Compaction = result;
            draft.CompactionTrace = new CompactionTrace(
                StepsEvicted: trimCount,
                TokensReclaimed: tokensReclaimed,
                // A strategy that fails open by handing the prior summary back has not folded the
                // new slice, so an error never counts as a fold.
                Folded: result.Error is null && result.UpdatedSummary is not null,
                Usage: result.Usage,
                Cost: result.Cost)
            {
                Error = result.Error
            };
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
            draft.TrajectoryMessages.Add(new Message(MessageRole.System, OriginalGoal(state.TaskText)));

        // The latest user turn is gone from the live groups (trimmed this turn, or behind the fold
        // watermark): without this the model cannot tell what the user asked.
        if (currentRequest is not null && !liveGroups.Any(g => ReferenceEquals(g.Step, currentRequest)))
            draft.TrajectoryMessages.Add(new Message(MessageRole.System, CurrentRequest(currentRequest.Content)));

        foreach (var (_, messages) in liveGroups)
            draft.TrajectoryMessages.AddRange(messages);

        // When an intervention note is the final turn, the request would end on an assistant turn.
        // Newer Claude models reject that (the retired prefill behaviour) with a 400. Keep the note
        // assistant-role — the model still "owns" the correction (self-consistency) — and append a
        // trailing user turn that both satisfies the required final role and restates the directive,
        // so the external command and the self-commitment reinforce each other. The wording follows
        // the notes it closes: advisory notes block nothing, so they get no demand for compliance —
        // but a blocking note earlier in the same trailing run (a PreReturn challenge followed by a
        // PreModelCall note on the retry) still has to be complied with.
        var trailingNotes = liveGroups
            .Select(g => g.Step)
            .Reverse()
            .TakeWhile(s => s is SensorInterventionStep)
            .Cast<SensorInterventionStep>()
            .ToList();
        if (trailingNotes.Count > 0)
            draft.TrajectoryMessages.Add(new Message(MessageRole.User,
                trailingNotes.All(n => n.HookPoint.IsAdvisory())
                    ? "Proceed, taking the harness observation above into account."
                    : "Proceed, fully complying with the harness observation above."));
    }

    // One definition so the text charged to the eviction budget and the text rendered are the same
    // string — counting one thing and emitting another is exactly how the window overshoot arose.
    private static string OriginalGoal(string taskText) => $"[ORIGINAL GOAL] {taskText}";

    private static string CurrentRequest(string userMessage) => $"[CURRENT REQUEST] {userMessage}";

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
                    var acknowledgement = si.HookPoint.IsAdvisory()
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
