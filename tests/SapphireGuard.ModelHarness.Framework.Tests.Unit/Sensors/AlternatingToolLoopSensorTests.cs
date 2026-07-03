using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class AlternatingToolLoopSensorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ToolCallStep ToolStep(string name, string argsJson = "{}") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), name, JsonDocument.Parse(argsJson).RootElement),
            new ToolResult("id", "result"));

    private static readonly AlternatingToolLoopSensor Sut = new(minCycles: 2);

    [Fact]
    public async Task AbabPattern_Intervenes()
    {
        var current = ToolStep("write", """{"v":1}""");
        var state = EmptyState()
            .AppendStep(ToolStep("read"))
            .AppendStep(ToolStep("write", """{"v":1}"""))
            .AppendStep(ToolStep("read"));

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("write", result.Reason);
    }

    [Fact]
    public async Task SameCallRepeated_Passes_LeftToStuckDetector()
    {
        var current = ToolStep("write", """{"v":1}""");
        var state = EmptyState()
            .AppendStep(ToolStep("write", """{"v":1}"""))
            .AppendStep(ToolStep("write", """{"v":1}"""))
            .AppendStep(ToolStep("write", """{"v":1}"""));

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task NotEnoughHistory_Passes()
    {
        var current = ToolStep("write");
        var state = EmptyState().AppendStep(ToolStep("read"));

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task BrokenPattern_Passes()
    {
        var current = ToolStep("write", """{"v":1}""");
        var state = EmptyState()
            .AppendStep(ToolStep("delete"))
            .AppendStep(ToolStep("write", """{"v":1}"""))
            .AppendStep(ToolStep("read"));

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, current, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task NonToolTrigger_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, EmptyState(), triggeringStep: null, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task CustomMinCycles_OneCycleAlternation_Intervenes()
    {
        var sut = new AlternatingToolLoopSensor(minCycles: 1);
        var current = ToolStep("write");
        var state = EmptyState().AppendStep(ToolStep("read"));

        var result = await sut.CheckAsync(HookPoint.PreToolCall, state, current, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }
}
