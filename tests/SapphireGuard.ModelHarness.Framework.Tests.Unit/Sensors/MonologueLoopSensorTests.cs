using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class MonologueLoopSensorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep ModelStep(string text, bool withToolCall = false) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = text,
                ToolCalls = withToolCall
                    ? [new ToolCall("id", "act", JsonDocument.Parse("{}").RootElement)]
                    : [],
                StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m
            },
            Usage: Usage.Zero, Cost: 0m);

    private static readonly MonologueLoopSensor Sut = new(repeatThreshold: 3);

    [Fact]
    public async Task IdenticalResponsesSplitAcrossUserTurns_Passes()
    {
        // Repeating an answer because the user asked again is not a monologue loop — the streak
        // resets at the latest user turn.
        var current = ModelStep("the answer is 42");
        var state = EmptyState()
            .AppendStep(ModelStep("the answer is 42"))
            .AppendStep(ModelStep("the answer is 42"))
            .WithUserMessage("sorry, what was the answer again?", DateTimeOffset.UtcNow)
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task ThreeIdenticalNoToolResponses_Intervenes()
    {
        var current = ModelStep("the answer is 42");
        var state = EmptyState()
            .AppendStep(ModelStep("the answer is 42"))
            .AppendStep(ModelStep("the answer is 42"))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task TwoIdentical_BelowThreshold_Passes()
    {
        var current = ModelStep("same");
        var state = EmptyState().AppendStep(ModelStep("same")).AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task DifferentText_BreaksStreak_Passes()
    {
        var current = ModelStep("answer B");
        var state = EmptyState()
            .AppendStep(ModelStep("answer A"))
            .AppendStep(ModelStep("answer A"))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task WhitespaceAndCaseDiffer_StillCountsAsRepeat()
    {
        var current = ModelStep("The  Answer");
        var state = EmptyState()
            .AppendStep(ModelStep("the answer"))
            .AppendStep(ModelStep("THE   answer"))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task CurrentResponseHasToolCall_Passes()
    {
        var current = ModelStep("acting now", withToolCall: true);
        var state = EmptyState()
            .AppendStep(ModelStep("acting now"))
            .AppendStep(ModelStep("acting now"))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PriorResponseWithToolCall_BreaksStreak_Passes()
    {
        var current = ModelStep("x");
        var state = EmptyState()
            .AppendStep(ModelStep("x"))
            .AppendStep(ModelStep("x", withToolCall: true))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostModelCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task NonModelTrigger_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, EmptyState(), triggeringStep: null, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }
}
