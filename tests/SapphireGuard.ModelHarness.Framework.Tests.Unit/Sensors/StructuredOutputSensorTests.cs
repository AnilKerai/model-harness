using SapphireGuard.ModelHarness.Framework.Output;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class StructuredOutputSensorTests
{
    private sealed record Triage(string Category, int Priority);

    private static readonly StructuredOutputSensor<Triage> Sut = new(new StructuredOutputContract<Triage>());

    private static AgentState State() => AgentState.NewTask("triage this", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep Answer(string? text) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = text, ToolCalls = [], StopReason = StopReason.EndTurn,
                Usage = Usage.Zero, Cost = 0m
            },
            Usage: Usage.Zero, Cost: 0m);

    private static Task<SensorResult> Check(ModelCallStep? step) =>
        Sut.CheckAsync(HookPoint.PreReturn, State(), step, CancellationToken.None);

    [Fact]
    public void HookPoints_IsPreReturnOnly()
    {
        // PreReturn is the only hookpoint the loop reaches on a turn with no tool calls — enforcing
        // the contract anywhere else would constrain intermediate reasoning turns.
        Assert.Equal([HookPoint.PreReturn], Sut.HookPoints);
    }

    [Fact]
    public async Task Check_AnswerSatisfiesContract_Passes()
    {
        var result = await Check(Answer("""{"category":"billing","priority":2}"""));

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_AnswerIsProse_Intervenes()
    {
        var result = await Check(Answer("This ticket is about billing and looks fairly urgent."));

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_AnswerIsIncomplete_Intervenes()
    {
        var result = await Check(Answer("""{"category":"billing"}"""));

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_Intervention_SuppressesToolsSoTheRepairTurnCanOnlyReformat()
    {
        var result = await Check(Answer("nope"));

        Assert.True(result.IsIntervene);
        Assert.True(result.SuppressTools);
    }

    [Fact]
    public async Task Check_Intervention_FeedsTheBindersOwnErrorBackToTheModel()
    {
        var result = await Check(Answer("""{"category":"billing"}"""));

        Assert.Contains("riority", result.Reason);   // the missing member, named by the binder
    }

    [Fact]
    public async Task Check_NoTriggeringStep_Passes()
    {
        var result = await Check(null);

        Assert.False(result.IsIntervene);
    }
}
