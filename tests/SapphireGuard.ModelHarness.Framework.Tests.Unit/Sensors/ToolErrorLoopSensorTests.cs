using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class ToolErrorLoopSensorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ToolCallStep ToolStep(string name, bool isError, string argsJson = "{}") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), name, JsonDocument.Parse(argsJson).RootElement),
            new ToolResult("id", isError ? "boom" : "ok", IsError: isError));

    private static readonly ToolErrorLoopSensor Sut = new(errorThreshold: 3);

    [Fact]
    public async Task ErrorsSplitAcrossUserTurns_Passes()
    {
        // Failures from a previous user turn shouldn't count toward this turn's streak — the user
        // may have supplied new information that makes the retry reasonable.
        var current = ToolStep("search", isError: true, argsJson: """{"q":"c"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true, argsJson: """{"q":"a"}"""))
            .AppendStep(ToolStep("search", isError: true, argsJson: """{"q":"b"}"""))
            .WithUserMessage("try searching for something else", DateTimeOffset.UtcNow)
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task ThreeErrorsSameTool_VaryingArgs_Intervenes()
    {
        var current = ToolStep("search", isError: true, argsJson: """{"q":"c"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true, argsJson: """{"q":"a"}"""))
            .AppendStep(ToolStep("search", isError: true, argsJson: """{"q":"b"}"""))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("search", result.Reason);
    }

    [Fact]
    public async Task TwoErrors_BelowThreshold_Passes()
    {
        var current = ToolStep("search", isError: true);
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task SuccessBetween_BreaksStreak_Passes()
    {
        var current = ToolStep("search", isError: true);
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true))
            .AppendStep(ToolStep("search", isError: false))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task DifferentToolBetween_BreaksStreak_Passes()
    {
        var current = ToolStep("search", isError: true);
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true))
            .AppendStep(ToolStep("lookup", isError: true))
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task NonToolStepsAreTransparent_Intervenes()
    {
        var sensorStep = new SensorInterventionStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            HookPoint.PostToolCall, "s", "r", null);
        var current = ToolStep("search", isError: true);
        var state = EmptyState()
            .AppendStep(ToolStep("search", isError: true))
            .AppendStep(ToolStep("search", isError: true))
            .AppendStep(sensorStep)
            .AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task CurrentResultNotError_Passes()
    {
        var current = ToolStep("search", isError: false);
        var state = EmptyState().AppendStep(current);

        var result = await Sut.CheckAsync(HookPoint.PostToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task NonToolTrigger_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PostToolCall, EmptyState(), triggeringStep: null, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }
}
