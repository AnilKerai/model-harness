using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Sensors;

public sealed class ToolResultSanityCheckSensorTests
{
    private static AgentState State() => AgentState.NewTask("t", new StateBudget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ToolCallStep Step(string content, bool isError = false) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), "my-tool", JsonDocument.Parse("{}").RootElement),
            new ToolResult("id", content, IsError: isError));

    private static readonly ToolResultSanityCheckSensor Default = new();

    [Fact]
    public async Task Check_NormalResult_Passes()
    {
        var result = await Default.CheckAsync(HookPoint.PostToolCall, State(), Step("some data"), CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_NonToolCallStep_Passes()
    {
        var result = await Default.CheckAsync(HookPoint.PostToolCall, State(), triggeringStep: null, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_IsError_Intervenes()
    {
        var result = await Default.CheckAsync(HookPoint.PostToolCall, State(), Step("boom", isError: true), CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("error", result.Reason);
    }

    [Fact]
    public async Task Check_EmptyResult_Intervenes()
    {
        var result = await Default.CheckAsync(HookPoint.PostToolCall, State(), Step(""), CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("empty", result.Reason);
    }

    [Fact]
    public async Task Check_WhitespaceResult_Intervenes()
    {
        var result = await Default.CheckAsync(HookPoint.PostToolCall, State(), Step("   "), CancellationToken.None);
        Assert.True(result.IsIntervene);
    }

    [Fact]
    public async Task Check_ResultExceedsMaxLength_Intervenes()
    {
        var sut = new ToolResultSanityCheckSensor(maxResultLength: 10);
        var result = await sut.CheckAsync(HookPoint.PostToolCall, State(), Step(new string('x', 11)), CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("character", result.Reason);
    }

    [Fact]
    public async Task Check_ResultAtExactMaxLength_Passes()
    {
        var sut = new ToolResultSanityCheckSensor(maxResultLength: 5);
        var result = await sut.CheckAsync(HookPoint.PostToolCall, State(), Step("hello"), CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_CustomValidatorFails_Intervenes()
    {
        var validators = new Dictionary<string, Func<string, string?>>
        {
            ["my-tool"] = v => v == "bad" ? "value is bad" : null
        };
        var sut = new ToolResultSanityCheckSensor(toolValidators: validators);

        var result = await sut.CheckAsync(HookPoint.PostToolCall, State(), Step("bad"), CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("value is bad", result.Reason);
    }

    [Fact]
    public async Task Check_CustomValidatorPasses_Passes()
    {
        var validators = new Dictionary<string, Func<string, string?>>
        {
            ["my-tool"] = v => null
        };
        var sut = new ToolResultSanityCheckSensor(toolValidators: validators);

        var result = await sut.CheckAsync(HookPoint.PostToolCall, State(), Step("good data"), CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task Check_ValidatorForDifferentTool_NotApplied()
    {
        var validators = new Dictionary<string, Func<string, string?>>
        {
            ["other-tool"] = _ => "always fails"
        };
        var sut = new ToolResultSanityCheckSensor(toolValidators: validators);

        // step tool name is "my-tool", not "other-tool"
        var result = await sut.CheckAsync(HookPoint.PostToolCall, State(), Step("fine"), CancellationToken.None);

        Assert.False(result.IsIntervene);
    }
}
