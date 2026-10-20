using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class HeadEvictionTrajectoryGuideTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentState EmptyState() =>
        AgentState.NewTask("test", new StateBudget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static ModelCallStep ModelStep(string text, StopReason stopReason = StopReason.EndTurn) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = text,
                ToolCalls = [],
                StopReason = stopReason,
                Usage = Usage.Zero,
                Cost = 0m
            },
            Usage: Usage.Zero,
            Cost: 0m);

    private static SensorInterventionStep InterventionStep(HookPoint hookPoint, string reason = "blocked") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            HookPoint: hookPoint,
            SensorName: "test-sensor",
            Reason: reason,
            TriggeringStep: null);

    private static ToolCallStep ToolStep() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Call: new ToolCall(Guid.NewGuid().ToString("n"), "test-tool", JsonDocument.Parse("{}").RootElement),
            Result: new ToolResult("id", "result"));

    // Eviction is driven by the compaction window, not the run budget. Basic-rendering tests use a
    // large window (no eviction); compaction tests pass windowTokens: 1 to force it.
    private static async Task<ContextDraft> ContributeAsync(AgentState state, int windowTokens = 100_000, bool pinOriginalGoal = true)
    {
        var draft = new ContextDraft();
        await new HeadEvictionTrajectoryGuide(new NullCompactionStrategy(), new CompactionOptions { WindowTokens = windowTokens }, pinOriginalGoal)
            .ContributeAsync(draft, state, CancellationToken.None);
        return draft;
    }

    [Fact]
    public async Task Contribute_GoalAnchorIsChargedToTheEvictionBudget()
    {
        // The [ORIGINAL GOAL] line is appended *after* the trim but rides in the same window, so it has
        // to be charged to the eviction budget. Uncharged, the rendered context overshot WindowTokens by
        // the task text's size on every compacted turn — inert while the adapters dropped all but the
        // first System message, real once DefaultContextBuilder began folding them into it.
        const int window = 1_400;
        var state = AgentState.NewTask(new string('g', 4_000), new StateBudget
        {
            MaxTurns = 100, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);
        for (var i = 0; i < 40; i++)
            state = state.AppendStep(ModelStep($"turn {i} did some work"));

        var pinned = await ContributeAsync(state, windowTokens: window, pinOriginalGoal: true);

        var rendered = RenderedTokens(pinned);
        Assert.True(rendered <= window,
            $"rendered {rendered} tokens into a {window}-token window — the goal anchor is not being charged for");

        // Control: without the anchor the same history fits the window with every answer kept, so the
        // trimming above is the anchor making room for itself rather than the history being too big.
        // (Chat mode reserves room for a [CURRENT REQUEST] note instead, which moves the oversized
        // opener into the system message — hence the count of answers, not of all messages.)
        var unpinned = await ContributeAsync(state, windowTokens: window, pinOriginalGoal: false);
        Assert.Equal(40, unpinned.TrajectoryMessages.Count(m => m.Role == MessageRole.Assistant));
    }

    private static int RenderedTokens(ContextDraft draft) =>
        draft.TrajectoryMessages.Sum(m => (m.Content.Length + 3) / 4);

    // ── Basic rendering ───────────────────────────────────────────────────────

    [Fact]
    public async Task Contribute_ModelStep_RendersAsAssistantMessage()
    {
        var state = EmptyState().AppendStep(ModelStep("hello"));
        var draft = await ContributeAsync(state);

        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.Assistant && m.Content == "hello");
    }

    [Fact]
    public async Task Contribute_MultipleUserMessages_RenderInOrderAsUserMessages()
    {
        // The seeded task plus an appended follow-up both survive, in order — the core
        // multi-turn property: more than one user message can live in the trajectory.
        var state = EmptyState() // seeds UserMessageStep("test")
            .AppendStep(ModelStep("answer 1"))
            .AppendStep(new UserMessageStep(Guid.NewGuid(), DateTimeOffset.UtcNow, "follow-up"));

        var draft = await ContributeAsync(state);

        var userMessages = draft.TrajectoryMessages
            .Where(m => m.Role == MessageRole.User)
            .Select(m => m.Content)
            .ToList();
        Assert.Equal(new[] { "test", "follow-up" }, userMessages);
    }

    [Fact]
    public async Task Contribute_ToolStep_RendersPairedToolUseAndToolMessages()
    {
        var state = EmptyState().AppendStep(ToolStep());
        var draft = await ContributeAsync(state);

        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.ToolUse);
        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.Tool);
    }

    [Fact]
    public async Task Contribute_SensorInterventionStep_RendersAsAssistantMessage()
    {
        var state = EmptyState().AppendStep(InterventionStep(HookPoint.PreReturn, "quality check failed"));
        var draft = await ContributeAsync(state);

        var intervention = draft.TrajectoryMessages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Contains("quality check failed", intervention.Content);
    }

    [Fact]
    public async Task Contribute_InterventionAsFinalStep_AppendsTrailingUserTurn()
    {
        // Newer Claude models 400 on a request ending on an assistant turn (prefill). The note stays
        // assistant-role for self-consistency; a trailing user turn carries the required final role.
        var state = EmptyState().AppendStep(InterventionStep(HookPoint.PreModelCall, "goal drift"));
        var draft = await ContributeAsync(state);

        Assert.Contains(draft.TrajectoryMessages, m => m.Role == MessageRole.Assistant && m.Content.Contains("goal drift"));
        Assert.Equal(MessageRole.User, draft.TrajectoryMessages[^1].Role);
    }

    [Fact]
    public async Task Contribute_ModelAnswerAsFinalStep_DoesNotAppendTrailingUserTurn()
    {
        // Only a trailing intervention gets re-sent to the model; a plain final answer ends the run,
        // so it must not accrue a spurious nudge.
        var state = EmptyState().AppendStep(ModelStep("final answer"));
        var draft = await ContributeAsync(state);

        Assert.Equal(MessageRole.Assistant, draft.TrajectoryMessages[^1].Role);
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("Proceed, fully complying"));
    }

    // ── PostModelCall blocking — the core correctness case ────────────────────

    [Fact]
    public async Task Contribute_ModelStepIntervenedAtPostModelCall_SuppressesResponseText()
    {
        // The critical invariant: a blocked PostModelCall response must NOT appear
        // in the model's next context. The intervention note still appears so the
        // model knows why it was blocked, but the actual (e.g. PII) content is gone.
        var state = EmptyState()
            .AppendStep(ModelStep("sensitive content john@example.com"))
            .AppendStep(InterventionStep(HookPoint.PostModelCall, "pii detected"));

        var draft = await ContributeAsync(state);

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("john@example.com"));
        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.Assistant && m.Content.Contains("pii detected"));
    }

    [Fact]
    public async Task Contribute_ModelStepIntervenedAtPostModelCall_MultipleSensors_SuppressesResponseText()
    {
        var state = EmptyState()
            .AppendStep(ModelStep("bad content"))
            .AppendStep(InterventionStep(HookPoint.PostModelCall, "pii"))
            .AppendStep(InterventionStep(HookPoint.PostModelCall, "policy violation"));

        var draft = await ContributeAsync(state);

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("bad content"));
        Assert.Equal(2, draft.TrajectoryMessages.Count(m => m.Role == MessageRole.Assistant));
    }

    [Fact]
    public async Task Contribute_ModelStepNotIntervened_RendersResponseTextNormally()
    {
        // A PreReturn block follows, but that should NOT suppress the model text —
        // only PostModelCall blocks suppress.
        var state = EmptyState()
            .AppendStep(ModelStep("good answer"))
            .AppendStep(InterventionStep(HookPoint.PreReturn, "quality gate"));

        var draft = await ContributeAsync(state);

        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.Assistant && m.Content == "good answer");
    }

    [Fact]
    public async Task Contribute_SecondModelCallAfterBlock_RendersNormally()
    {
        // First call is blocked; second call (the clean retry) must be visible.
        var state = EmptyState()
            .AppendStep(ModelStep("bad content"))
            .AppendStep(InterventionStep(HookPoint.PostModelCall, "pii"))
            .AppendStep(ModelStep("clean answer"));

        var draft = await ContributeAsync(state);

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("bad content"));
        Assert.Single(draft.TrajectoryMessages, m => m.Role == MessageRole.Assistant && m.Content == "clean answer");
    }

    // ── Incremental fold compaction ───────────────────────────────────────────

    // WindowTokens = 1 forces the whole live tail to be evicted, so compaction always fires.
    private static HeadEvictionTrajectoryGuide EvictingGuide(ICompactionStrategy strategy) =>
        new(strategy, new CompactionOptions { WindowTokens = 1 });

    [Fact]
    public async Task Contribute_Fold_PassesOnlyNewSliceAndPriorSummary_SkipsFoldedHead()
    {
        // The "fold, not view" guarantee: with a prior summary covering the first 2 groups, only the
        // newly evicted tail is handed to the strategy — never the whole head again — and the
        // watermark advances by exactly that slice. Two live tail groups so one can be evicted while
        // the most-recent group is kept (eviction never empties the conversation).
        var strategy = new RecordingCompactionStrategy();
        var built = EmptyState()
            .AppendStep(ModelStep("FOLDED_M1"))
            .AppendStep(ModelStep("LIVE_TAIL_1"))
            .AppendStep(ModelStep("LIVE_TAIL_2"));
        var state = built with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 2) };

        var draft = new ContextDraft();
        await EvictingGuide(strategy).ContributeAsync(draft, state, CancellationToken.None);

        var call = Assert.Single(strategy.Calls);
        Assert.Equal("PRIOR SUMMARY", call.PriorSummary?.Text);
        Assert.DoesNotContain(call.EvictedSteps, s => s is UserMessageStep);       // folded head not re-evicted
        Assert.All(call.EvictedSteps, s => Assert.IsType<ModelCallStep>(s));       // only live-tail steps
        Assert.Equal(2 + call.EvictedSteps.Count, draft.Compaction!.UpdatedSummary!.FoldedStepCount);
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("FOLDED_M1"));
        // The most-recent group is never evicted, so it is still rendered.
        Assert.Contains(draft.TrajectoryMessages, m => m.Content.Contains("LIVE_TAIL_2"));
    }

    [Fact]
    public async Task Contribute_ViewStrategy_CompactsButPersistsNothing()
    {
        var state = EmptyState().AppendStep(ModelStep("some tail content to evict"));
        var draft = await ContributeAsync(state, windowTokens: 1); // uses NullCompactionStrategy (a view)

        Assert.NotNull(draft.Compaction);                 // compaction happened
        Assert.Null(draft.Compaction!.UpdatedSummary);    // but nothing to persist — stays a view
    }

    [Fact]
    public async Task Contribute_StrategyThrows_FailsOpenWithOmissionNoteAndDoesNotThrow()
    {
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new ThrowingCompactionStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.Contains("omitted", draft.Compaction!.InjectedText);
        Assert.Null(draft.Compaction.UpdatedSummary);
    }

    [Fact]
    public async Task Contribute_StrategyThrows_RecordsWhyOnTheCompactionTrace()
    {
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new ThrowingCompactionStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.Equal("InvalidOperationException: strategy boom", draft.CompactionTrace!.Error);
    }

    [Fact]
    public async Task Contribute_StrategyThrowsWithAPriorSummary_KeepsTheSummaryInContext()
    {
        // Steps behind the fold watermark are no longer rendered, so the rolling summary is the only
        // trace of them. A fallback that replaced it with a bare note would leave the model with
        // neither — on every turn, if the strategy keeps failing.
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new ThrowingCompactionStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        var rendered = Assert.Single(draft.TrajectoryMessages, m => m.Content.Contains("omitted"));
        Assert.Contains("PRIOR SUMMARY", rendered.Content);
    }

    [Fact]
    public async Task Contribute_ViewStrategyOverAStateWithARollingSummary_KeepsTheSummaryInContext()
    {
        // A checkpoint written under a fold strategy can be resumed with the default view strategy
        // wired. A view describes only the newly evicted slice, so the summary must still render.
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));

        var draft = await ContributeAsync(state, windowTokens: 1); // NullCompactionStrategy

        var rendered = Assert.Single(draft.TrajectoryMessages, m => m.Content.Contains("omitted"));
        Assert.Contains("PRIOR SUMMARY", rendered.Content);
    }

    [Fact]
    public async Task Contribute_Fold_RendersItsOwnTextInPlaceOfThePriorSummary()
    {
        // A fold returns the whole updated summary, so the prior one must not be rendered beside it.
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new RecordingCompactionStrategy()).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Contains(draft.TrajectoryMessages, m => m.Content == "SUMMARY");
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("PRIOR SUMMARY"));
    }

    [Fact]
    public async Task Contribute_ViewThatInjectsNothing_StillRendersThePriorSummaryAlone()
    {
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new SilentViewStrategy()).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Contains(draft.TrajectoryMessages, m => m.Content == "PRIOR SUMMARY");
    }

    [Fact]
    public async Task Contribute_StrategyReturnsNullText_DoesNotFaultTheRun()
    {
        // InjectedText is non-nullable, but a strategy compiled without nullable checks can still
        // hand back null. The trajectory guide is not wrapped by the runner, so throwing here would
        // end the run — render the prior summary and carry on.
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new NullTextStrategy()).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Contains(draft.TrajectoryMessages, m => m.Content == "PRIOR SUMMARY");
    }

    [Fact]
    public async Task Contribute_StrategyReportsItsOwnFailure_CarriesItOntoTheCompactionTrace()
    {
        // A strategy that fails open internally (AiCompactionStrategy keeps the prior summary) still
        // has to be visible: its reported error rides the trace exactly like a thrown one.
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new SelfReportingFailureStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.Equal("HttpRequestException: 503", draft.CompactionTrace!.Error);
    }

    [Fact]
    public async Task Contribute_FoldFailsButKeepsThePriorSummary_IsNotReportedAsFolded()
    {
        // AiCompactionStrategy fails open by handing the prior summary back unchanged. That is a
        // fallback, not a fold — the trace must not claim the newly evicted slice was folded.
        var state = (EmptyState() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 0) })
            .AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new SelfReportingFailureStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.False(draft.CompactionTrace!.Folded);
    }

    [Fact]
    public async Task Contribute_StrategySucceeds_LeavesTheTraceErrorNull()
    {
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new RecordingCompactionStrategy()).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Null(draft.CompactionTrace!.Error);
    }

    [Fact]
    public async Task Contribute_StrategyTimesOutOnItsOwn_FailsOpenInsteadOfCancellingTheRun()
    {
        // A strategy's own HTTP timeout surfaces as TaskCanceledException — an
        // OperationCanceledException — even though the harness token was never cancelled. It must
        // fall back to the omission note like any other fault, not end the run as "cancelled".
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new TimingOutCompactionStrategy())
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.Contains("omitted", draft.Compaction!.InjectedText);
        Assert.StartsWith("TaskCanceledException", draft.CompactionTrace!.Error);
    }

    [Fact]
    public async Task Contribute_HarnessCancellationRequested_StillPropagates()
    {
        // The fail-open filter must not swallow genuine cancellation — that is real control flow.
        var state = EmptyState().AppendStep(ModelStep("tail to evict"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EvictingGuide(new TimingOutCompactionStrategy()).ContributeAsync(new ContextDraft(), state, cts.Token));
    }

    [Fact]
    public async Task Contribute_WhenCompacting_PopulatesCompactionTrace()
    {
        var state = EmptyState()
            .AppendStep(ModelStep("tail one to evict"))
            .AppendStep(ModelStep("tail two to evict"));
        var draft = new ContextDraft();

        await EvictingGuide(new RecordingCompactionStrategy()).ContributeAsync(draft, state, CancellationToken.None);

        Assert.NotNull(draft.CompactionTrace);
        Assert.True(draft.CompactionTrace!.StepsEvicted > 0);
        Assert.True(draft.CompactionTrace.TokensReclaimed > 0);
        Assert.True(draft.CompactionTrace.Folded);   // RecordingCompactionStrategy returns an UpdatedSummary
    }

    [Fact]
    public async Task Contribute_NoEviction_LeavesCompactionTraceNull()
    {
        var state = EmptyState().AppendStep(ModelStep("small")); // large default window → no eviction
        var draft = await ContributeAsync(state);

        Assert.Null(draft.CompactionTrace);
    }

    // ── Chat mode: the latest user turn survives eviction ─────────────────────

    private const string Question = "Which of these invoices are overdue?";
    private const string CurrentRequestNote = "[CURRENT REQUEST] " + Question;

    private static ToolCallStep BulkyToolStep(int resultChars) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Call: new ToolCall(Guid.NewGuid().ToString("n"), "fetch", JsonDocument.Parse("{}").RootElement),
            Result: new ToolResult("id", new string('x', resultChars)));

    // A second chat turn whose tool output alone overflows a 500-token window (~1000 tokens), so the
    // earlier turn *and* the user message that opened this one are trimmed oldest-first.
    private static AgentState SecondTurnWithBulkyToolResult() =>
        EmptyState()
            .AppendStep(ModelStep("opening answer"))
            .WithUserMessage(Question, DateTimeOffset.UtcNow)
            .AppendStep(BulkyToolStep(4_000));

    [Fact]
    public async Task Contribute_ChatModeUserTurnEvicted_RendersItAsASystemCurrentRequest()
    {
        var draft = await ContributeAsync(SecondTurnWithBulkyToolResult(), windowTokens: 500, pinOriginalGoal: false);

        var note = Assert.Single(draft.TrajectoryMessages, m => m.Content.StartsWith("[CURRENT REQUEST]"));
        Assert.Equal(MessageRole.System, note.Role);
        Assert.Equal(CurrentRequestNote, note.Content);
        // Evicted from its position, so it is not also rendered as a user turn.
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Role == MessageRole.User);
        // The still-live tool exchange is untouched.
        Assert.Contains(draft.TrajectoryMessages, m => m.Role == MessageRole.Tool);
    }

    [Fact]
    public async Task Contribute_ChatModeUserTurnEvicted_OnlyTheLatestUserTurnIsRenderedAsCurrentRequest()
    {
        var draft = await ContributeAsync(SecondTurnWithBulkyToolResult(), windowTokens: 500, pinOriginalGoal: false);

        Assert.Single(draft.TrajectoryMessages, m => m.Content.Contains("[CURRENT REQUEST]"));
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("[ORIGINAL GOAL]"));
    }

    [Fact]
    public async Task BuildAsync_ChatModeUserTurnEvicted_PutsTheQuestionInTheSystemMessageNotTheConversation()
    {
        // End to end through the builder: the System-role note is folded into the single system
        // message (the only one an adapter forwards), and the conversation no longer carries the turn.
        var guide = new HeadEvictionTrajectoryGuide(
            new NullCompactionStrategy(), new CompactionOptions { WindowTokens = 500 }, pinOriginalGoal: false);
        var builder = new DefaultContextBuilder(new DefaultGuideRunner([], guide));

        var result = await builder.BuildAsync(SecondTurnWithBulkyToolResult(), [], CancellationToken.None);

        var system = Assert.Single(result.Messages, m => m.Role == MessageRole.System);
        Assert.Same(system, result.Messages[0]);
        Assert.Contains(CurrentRequestNote, system.Content);
        Assert.DoesNotContain(result.Messages.Skip(1), m => m.Content.Contains(Question));
    }

    [Fact]
    public async Task Contribute_ChatModeUserTurnBehindTheFoldWatermark_RendersItAsACurrentRequest()
    {
        // Groups: opener, answer, question, tool exchange. A watermark of 3 means the question is
        // represented only by the summary — which is lossy — so it must still reach the model verbatim.
        var state = SecondTurnWithBulkyToolResult() with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 3) };

        var draft = await ContributeAsync(state, pinOriginalGoal: false); // default large window → no trim

        Assert.Single(draft.TrajectoryMessages, m => m.Content == CurrentRequestNote);
        Assert.Contains(draft.TrajectoryMessages, m => m.Content == "PRIOR SUMMARY");
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Role == MessageRole.User);
    }

    [Fact]
    public async Task Contribute_ChatModeUserTurnEvicted_ChargesTheCurrentRequestToTheBudget()
    {
        // The [CURRENT REQUEST] note is added after the trim but rides in the same window, so it has to
        // be charged before trimming — as the goal anchor is. A 2,000-char question (~500 tokens)
        // followed by twenty ~10-token answers totals ~700 tokens against a 650-token window: the
        // question is evicted, and re-adding it uncharged would render ~690 tokens into 650.
        const int window = 650;
        var state = AgentState.NewTask(new string('q', 2_000), new StateBudget
        {
            MaxTurns = 100, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);
        for (var i = 0; i < 20; i++)
            state = state.AppendStep(ModelStep($"answer {i:00} with padding to ~40 chars...."));

        var draft = new ContextDraft();
        await new HeadEvictionTrajectoryGuide(new SilentViewStrategy(), new CompactionOptions { WindowTokens = window }, pinOriginalGoal: false)
            .ContributeAsync(draft, state, CancellationToken.None);

        Assert.Single(draft.TrajectoryMessages, m => m.Content.StartsWith("[CURRENT REQUEST]"));
        var rendered = RenderedTokens(draft);
        Assert.True(rendered <= window,
            $"rendered {rendered} tokens into a {window}-token window — the current request is not being charged for");
    }

    [Fact]
    public async Task Contribute_ChatModeUserTurnNotEvicted_RendersItInPositionWithNoCurrentRequest()
    {
        var draft = await ContributeAsync(SecondTurnWithBulkyToolResult(), pinOriginalGoal: false); // large window

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("[CURRENT REQUEST]"));
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Role == MessageRole.System);
        Assert.Equal(
            new[] { MessageRole.User, MessageRole.Assistant, MessageRole.User, MessageRole.ToolUse, MessageRole.Tool },
            draft.TrajectoryMessages.Select(m => m.Role));
        Assert.Equal(Question, draft.TrajectoryMessages[2].Content);
    }

    [Fact]
    public async Task Contribute_ChatModeNewTurnIsTheLastGroup_KeepsItInPositionEvenInATinyWindow()
    {
        // The question just arrived: it is the most recent group, which eviction never removes.
        var state = EmptyState()
            .AppendStep(ModelStep("opening answer"))
            .WithUserMessage(Question, DateTimeOffset.UtcNow);

        var draft = await ContributeAsync(state, windowTokens: 1, pinOriginalGoal: false);

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("[CURRENT REQUEST]"));
        Assert.Equal(Question, draft.TrajectoryMessages[^1].Content);
        Assert.Equal(MessageRole.User, draft.TrajectoryMessages[^1].Role);
    }

    [Fact]
    public async Task Contribute_TaskModeOversizedTurn_IsUnchanged()
    {
        // Task mode keeps the [ORIGINAL GOAL] pin and never renders a [CURRENT REQUEST]: the same
        // oversized state evicts exactly as before (the omission note, the pinned goal, then the
        // still-live tool exchange).
        var draft = await ContributeAsync(SecondTurnWithBulkyToolResult(), windowTokens: 500, pinOriginalGoal: true);

        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("[CURRENT REQUEST]"));
        Assert.Equal(
            new[] { MessageRole.System, MessageRole.System, MessageRole.ToolUse, MessageRole.Tool },
            draft.TrajectoryMessages.Select(m => m.Role));
        Assert.Contains("omitted", draft.TrajectoryMessages[0].Content);
        Assert.Equal("[ORIGINAL GOAL] test", draft.TrajectoryMessages[1].Content);
    }

    // ── Never-empty conversation (empty messages array is a provider 400) ──────

    [Fact]
    public async Task Contribute_TinyWindow_KeepsAtLeastOneLiveGroup()
    {
        // A window too small to fit anything must still leave the most-recent group, so the rendered
        // conversation has at least one non-System message.
        var state = EmptyState()
            .AppendStep(ModelStep("m1"))
            .AppendStep(ModelStep("m2"))
            .AppendStep(ModelStep("m3"));

        var draft = await ContributeAsync(state, windowTokens: 1);

        Assert.Contains(draft.TrajectoryMessages, m => m.Role != MessageRole.System);
    }

    [Fact]
    public async Task Contribute_FoldWatermarkCoversAllGroups_StillRendersAConversation()
    {
        // Resuming a checkpoint whose fold watermark already covers every rendered group: the live
        // set starts empty, but the guide must still render the most-recent group rather than emit an
        // empty conversation.
        var built = EmptyState().AppendStep(ModelStep("recent answer")); // 2 groups (user + model)
        var state = built with { RollingSummary = new RollingSummary("covers everything", 2) };

        var draft = await ContributeAsync(state); // default large window

        Assert.Contains(draft.TrajectoryMessages, m => m.Role != MessageRole.System);
    }

    private sealed class RecordingCompactionStrategy : ICompactionStrategy
    {
        public List<CompactionRequest> Calls { get; } = [];

        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            var folded = (request.PriorSummary?.FoldedStepCount ?? 0) + request.EvictedSteps.Count;
            return Task.FromResult(new CompactionResult
            {
                InjectedText = "SUMMARY",
                UpdatedSummary = new RollingSummary("SUMMARY", folded)
            });
        }
    }

    private sealed class ThrowingCompactionStrategy : ICompactionStrategy
    {
        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("strategy boom");
    }

    private sealed class TimingOutCompactionStrategy : ICompactionStrategy
    {
        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
    }

    // A view that only drops steps: persists nothing and injects nothing.
    private sealed class SilentViewStrategy : ICompactionStrategy
    {
        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
            Task.FromResult(new CompactionResult { InjectedText = "" });
    }

    private sealed class NullTextStrategy : ICompactionStrategy
    {
        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
            Task.FromResult(new CompactionResult { InjectedText = null! });
    }

    // Mirrors how AiCompactionStrategy fails open: prior summary handed back, error reported.
    private sealed class SelfReportingFailureStrategy : ICompactionStrategy
    {
        public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
            Task.FromResult(new CompactionResult
            {
                InjectedText = "[steps omitted — compaction unavailable]",
                UpdatedSummary = request.PriorSummary,
                Error = "HttpRequestException: 503"
            });
    }
}
