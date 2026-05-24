using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class StuckDetectorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxContextTokens = 100_000, MaxCostUsd = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    });

    private static ToolCallStep ToolStep(string name, string argsJson = "{}") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), name, JsonDocument.Parse(argsJson).RootElement),
            new ToolResult("id", "result"));

    private static readonly StuckDetector Sut = new(repeatThreshold: 3);

    [Fact]
    public async Task Check_NonToolCallTrigger_Passes()
    {
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, EmptyState(), triggeringStep: null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_FirstCall_Passes()
    {
        var step = ToolStep("search");
        var state = EmptyState();

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_RepeatsBelowThreshold_Passes()
    {
        var step = ToolStep("search", """{"q":"hello"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", """{"q":"hello"}"""))
            .AppendStep(ToolStep("search", """{"q":"hello"}"""));

        // Two in trajectory + this one = 3 total, but threshold is 3 so consecutive must reach 3
        // State has 2 prior, current makes 3 — should intervene at threshold exactly
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("search", result.Reason);
    }

    [Fact]
    public async Task Check_DifferentArgs_DoesNotCountAsRepeat()
    {
        var step = ToolStep("search", """{"q":"world"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", """{"q":"hello"}"""))
            .AppendStep(ToolStep("search", """{"q":"hello"}"""));

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_NonToolStepInTrajectory_SkippedDuringCount()
    {
        // StuckDetector skips non-ToolCallStep entries with `continue`, not `break`.
        // A SensorInterventionStep between identical tool calls does NOT reset the count.
        var sensorStep = new SensorInterventionStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            HookPoint.PostToolCall, "s", "r", null);
        var step = ToolStep("search", """{"q":"hello"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", """{"q":"hello"}"""))
            .AppendStep(ToolStep("search", """{"q":"hello"}"""))
            .AppendStep(sensorStep);

        // 2 in trajectory + current = 3 → threshold reached (non-tool step is skipped, not a break)
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_DifferentToolCallBreaksChain_Passes()
    {
        // A different tool call mid-trajectory DOES break the chain because its signature
        // doesn't match, causing the backward scan to stop.
        var step = ToolStep("search", """{"q":"hello"}""");
        var state = EmptyState()
            .AppendStep(ToolStep("search", """{"q":"hello"}"""))
            .AppendStep(ToolStep("lookup", """{"id":"1"}""")); // different tool — breaks chain

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_CustomThreshold_RespectsIt()
    {
        var sut = new StuckDetector(repeatThreshold: 2);
        var step = ToolStep("ping");
        var state = EmptyState().AppendStep(ToolStep("ping"));

        var result = await sut.CheckAsync(HookPoint.PreToolCall, state, step, CancellationToken.None);

        Assert.True(result.IsIntervene);
    }
}
