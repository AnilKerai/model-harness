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
    private static async Task<ContextDraft> ContributeAsync(AgentState state, int windowTokens = 100_000)
    {
        var draft = new ContextDraft();
        await new HeadEvictionTrajectoryGuide(new NullCompactionStrategy(), new CompactionOptions { WindowTokens = windowTokens })
            .ContributeAsync(draft, state, CancellationToken.None);
        return draft;
    }

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
        // watermark advances by exactly that slice.
        var strategy = new RecordingCompactionStrategy();
        var built = EmptyState()
            .AppendStep(ModelStep("FOLDED_M1"))
            .AppendStep(ModelStep("LIVE_TAIL"));
        var state = built with { RollingSummary = new RollingSummary("PRIOR SUMMARY", 2) };

        var draft = new ContextDraft();
        await EvictingGuide(strategy).ContributeAsync(draft, state, CancellationToken.None);

        var call = Assert.Single(strategy.Calls);
        Assert.Equal("PRIOR SUMMARY", call.PriorSummary?.Text);
        Assert.DoesNotContain(call.EvictedSteps, s => s is UserMessageStep);       // folded head not re-evicted
        Assert.All(call.EvictedSteps, s => Assert.IsType<ModelCallStep>(s));       // only live-tail steps
        Assert.Equal(2 + call.EvictedSteps.Count, draft.Compaction!.UpdatedSummary!.FoldedStepCount);
        Assert.DoesNotContain(draft.TrajectoryMessages, m => m.Content.Contains("FOLDED_M1"));
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
