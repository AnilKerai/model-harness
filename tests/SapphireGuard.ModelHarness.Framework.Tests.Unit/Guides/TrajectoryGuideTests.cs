using System.Text.Json;
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

        // Control: without the anchor the same state fits the window untrimmed (1 user + 40 assistant),
        // so the trimming above is the anchor making room for itself rather than the history being too big.
        var unpinned = await ContributeAsync(state, windowTokens: window, pinOriginalGoal: false);
        Assert.Equal(41, unpinned.TrajectoryMessages.Count);
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
}
