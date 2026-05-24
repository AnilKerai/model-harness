using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class TrajectoryGuideTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentState EmptyState() =>
        AgentState.NewTask("test", new StateBudget
        {
            MaxTurns = 10,
            MaxContextTokens = 100_000,
            MaxCostUsd = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });

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

    private static async Task<ContextDraft> ContributeAsync(AgentState state)
    {
        var draft = new ContextDraft();
        await new TrajectoryGuide().ContributeAsync(draft, state, CancellationToken.None);
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
}
