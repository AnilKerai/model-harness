using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Sensors;

public sealed class PiiRedactionSensorTests
{
    private static readonly PiiRedactionSensor Sut = new();

    private static AgentState State() => AgentState.NewTask("t", new StateBudget
    {
        MaxTurns = 10, MaxContextTokens = 100_000, MaxCostUsd = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    });

    private static ModelCallStep ModelStep(string? text) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, [], new ModelResponse
        {
            Text = text, ToolCalls = [], StopReason = StopReason.EndTurn,
            Usage = Usage.Zero, Cost = 0m
        }, Usage.Zero, 0m);

    [Fact]
    public async Task Check_CleanText_Passes()
    {
        var step = ModelStep("The answer is 42.");
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, State(), step, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_NullText_Passes()
    {
        var step = ModelStep(null);
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, State(), step, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_NonModelCallStep_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, State(), triggeringStep: null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    // Patterns tested with label assertion — inputs that won't match an earlier pattern in the array.
    [Theory]
    [InlineData("Contact john@example.com for details.", "email")]
    [InlineData("Call us at +44 7911 123456 anytime.", "phone")]
    [InlineData("NI number: AB123456C", "uk-ni")]
    public async Task Check_PiiPattern_IntervenesWithLabel(string text, string expectedLabel)
    {
        var step = ModelStep(text);
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, State(), step, CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains(expectedLabel, result.Reason);
    }

    // Credit card and SSN digit sequences also match the broad phone regex, so we only assert
    // that the sensor intervenes — not which specific label fires first.
    [Theory]
    [InlineData("Card: 4111 1111 1111 1111")]
    [InlineData("SSN: 123-45-6789")]
    public async Task Check_PiiPattern_Intervenes(string text)
    {
        var step = ModelStep(text);
        var result = await Sut.CheckAsync(HookPoint.PostModelCall, State(), step, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }
}
