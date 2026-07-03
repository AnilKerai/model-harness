using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Persistence;

public sealed class StepJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new StepJsonConverter() }
    };

    private static Step RoundTrip(Step step)
    {
        var json = JsonSerializer.Serialize(step, Options);
        return JsonSerializer.Deserialize<Step>(json, Options)!;
    }

    [Fact]
    public void ModelCallStep_round_trips()
    {
        var step = new ModelCallStep(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            [new Message(MessageRole.User, "hello")],
            new ModelResponse { Text = "hi", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m },
            Usage.Zero,
            Cost: 0m);

        var result = Assert.IsType<ModelCallStep>(RoundTrip(step));
        Assert.Equal(step.Id, result.Id);
        Assert.Equal(step.Cost, result.Cost);
        Assert.Single(result.Prompt);
    }

    [Fact]
    public void ToolCallStep_round_trips()
    {
        var args = JsonDocument.Parse("""{"x":1}""").RootElement;
        var step = new ToolCallStep(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new ToolCall("call-1", "my-tool", args),
            new ToolResult("call-1", "done"));

        var result = Assert.IsType<ToolCallStep>(RoundTrip(step));
        Assert.Equal(step.Id, result.Id);
        Assert.Equal(step.Call.ToolName, result.Call.ToolName);
        Assert.Equal(step.Call.CallId, result.Call.CallId);
        Assert.Equal(step.Result.Content, result.Result.Content);
    }

    [Fact]
    public void SensorInterventionStep_round_trips()
    {
        var step = new SensorInterventionStep(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            HookPoint.PreToolCall,
            "my-sensor",
            "dangerous call blocked",
            TriggeringStep: null);

        var result = Assert.IsType<SensorInterventionStep>(RoundTrip(step));
        Assert.Equal(step.Id, result.Id);
        Assert.Equal(step.Reason, result.Reason);
        Assert.Equal(step.SensorName, result.SensorName);
        Assert.Equal(step.HookPoint, result.HookPoint);
        Assert.Null(result.TriggeringStep);
    }

    [Fact]
    public void UserMessageStep_round_trips()
    {
        var step = new UserMessageStep(Guid.NewGuid(), DateTimeOffset.UtcNow, "follow-up question");

        var result = Assert.IsType<UserMessageStep>(RoundTrip(step));
        Assert.Equal(step.Id, result.Id);
        Assert.Equal(step.Content, result.Content);
    }

    [Fact]
    public void Read_throws_on_missing_type_discriminator()
    {
        var json = """{"Id":"00000000-0000-0000-0000-000000000001","Timestamp":"2024-01-01T00:00:00+00:00"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Step>(json, Options));
    }

    [Fact]
    public void Read_throws_on_unknown_type_discriminator()
    {
        var json = """{"$type":"UnknownStep","Id":"00000000-0000-0000-0000-000000000001"}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Step>(json, Options));
    }

    [Fact]
    public void Checkpoint_with_rolling_summary_and_compaction_spend_round_trips()
    {
        // The resume-without-recompute guarantee: the folded summary and its watermark must survive
        // a checkpoint so a reloaded run folds onward instead of re-summarising from scratch.
        var budget = new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(5)
        };
        var state = AgentState.NewTask("task", budget, DateTimeOffset.UtcNow) with
        {
            RollingSummary = new RollingSummary("folded so far", 7),
            CompactionUsage = new Usage(500, 120),
            CompactionCost = 0.25m,
            Pins = [new PinnedNote("Skill: verify", "the pinned procedure body")]
        };
        var checkpoint = new Checkpoint
        {
            CheckpointId = "c1", RunId = "r1", CreatedAt = DateTimeOffset.UtcNow, TurnNumber = 3, State = state
        };

        var restored = CheckpointSerializer.Deserialize(CheckpointSerializer.Serialize(checkpoint))!;

        Assert.Equal("folded so far", restored.State.RollingSummary?.Text);
        Assert.Equal(7, restored.State.RollingSummary?.FoldedStepCount);
        Assert.Equal(0.25m, restored.State.CompactionCost);
        Assert.Equal(620, restored.State.CompactionUsage.TotalTokens);
        var pin = Assert.Single(restored.State.Pins);
        Assert.Equal("Skill: verify", pin.Label);
        Assert.Equal("the pinned procedure body", pin.Content);
    }
}
