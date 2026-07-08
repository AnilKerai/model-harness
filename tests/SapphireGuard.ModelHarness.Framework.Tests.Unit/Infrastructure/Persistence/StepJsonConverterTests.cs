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

    [Fact]
    public void Checkpoint_with_every_AgentState_field_populated_round_trips()
    {
        // Full-fidelity checkpoint contract: a resumed run must rehydrate the whole AgentState, not a
        // subset. Every field is set to a non-default value and a mixed trajectory carries one of each
        // Step type, so this fails the moment any field or step type stops surviving serialization.
        var budget = new StateBudget
        {
            MaxTurns = 12, MaxTotalTokens = 200_000, MaxCost = 5m,
            MaxWallClock = TimeSpan.FromMinutes(15), MaxToolCallDuration = TimeSpan.FromSeconds(30)
        };
        var t0 = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var args = JsonDocument.Parse("""{"order":"A1001"}""").RootElement;

        var trajectory = new Step[]
        {
            new UserMessageStep(Guid.NewGuid(), t0, "where is my order?"),
            new ModelCallStep(
                Guid.NewGuid(), t0.AddSeconds(1),
                [new Message(MessageRole.System, "sys"), new Message(MessageRole.User, "where is my order?")],
                new ModelResponse
                {
                    Text = "let me check",
                    ToolCalls = [new ToolCall("call-1", "get_order_status", args)],
                    StopReason = StopReason.ToolUse,
                    Usage = new Usage(120, 45), Cost = 0.02m,
                    Model = "claude-haiku-4-5", Provider = "anthropic",
                    CachedInputTokens = 80, CacheWriteTokens = 40
                },
                new Usage(120, 45), Cost: 0.02m),
            new ToolCallStep(
                Guid.NewGuid(), t0.AddSeconds(2),
                new ToolCall("call-1", "get_order_status", args),
                new ToolResult("call-1", "shipped", Pins: [new PinnedNote("Skill: order", "body")])),
            new SensorInterventionStep(
                Guid.NewGuid(), t0.AddSeconds(3),
                HookPoint.PostModelCall, "pii-redaction", "email leaked",
                TriggeringStep: new UserMessageStep(Guid.NewGuid(), t0, "trigger"))
        };

        var state = new AgentState
        {
            TaskId = "job-42", TaskText = "where is my order?", Budget = budget,
            Status = AgentStatus.AwaitingHuman,
            Trajectory = trajectory,
            Metadata = new Dictionary<string, string> { ["channel"] = "email", ["priority"] = "high" },
            Pins = [new PinnedNote("Skill: order", "the pinned body")],
            SensorUsage = new Usage(200, 60), SensorCost = 0.11m,
            RollingSummary = new RollingSummary("folded", 3),
            CompactionUsage = new Usage(300, 90), CompactionCost = 0.22m,
            PendingHumanInput = new PendingHumanInput("call-9", "need a refund approval?")
        };
        var checkpoint = new Checkpoint
        {
            CheckpointId = "c1", RunId = "r1", CreatedAt = t0, TurnNumber = 4, State = state
        };

        var json = CheckpointSerializer.Serialize(checkpoint);
        var restored = CheckpointSerializer.Deserialize(json)!;
        var s = restored.State;

        Assert.Equal("job-42", s.TaskId);
        Assert.Equal("where is my order?", s.TaskText);
        Assert.Equal(AgentStatus.AwaitingHuman, s.Status);
        Assert.Equal(budget, s.Budget); // record equality also covers the optional MaxToolCallDuration

        Assert.Equal("email", s.Metadata["channel"]);
        Assert.Equal("high", s.Metadata["priority"]);

        var pin = Assert.Single(s.Pins);
        Assert.Equal("Skill: order", pin.Label);
        Assert.Equal("the pinned body", pin.Content);

        Assert.Equal(new Usage(200, 60), s.SensorUsage);
        Assert.Equal(0.11m, s.SensorCost);
        Assert.Equal(new Usage(300, 90), s.CompactionUsage);
        Assert.Equal(0.22m, s.CompactionCost);
        Assert.Equal("folded", s.RollingSummary!.Text);
        Assert.Equal(3, s.RollingSummary.FoldedStepCount);

        Assert.Equal("call-9", s.PendingHumanInput!.CallId);
        Assert.Equal("need a refund approval?", s.PendingHumanInput.Question);

        Assert.Equal(4, s.Trajectory.Count);
        Assert.IsType<UserMessageStep>(s.Trajectory[0]);
        var model = Assert.IsType<ModelCallStep>(s.Trajectory[1]);
        Assert.Equal(2, model.Prompt.Count);
        Assert.Equal(StopReason.ToolUse, model.Response.StopReason);
        Assert.Equal("claude-haiku-4-5", model.Response.Model);
        Assert.Equal("anthropic", model.Response.Provider);
        Assert.Equal(80, model.Response.CachedInputTokens);
        Assert.Equal(40, model.Response.CacheWriteTokens);
        var tool = Assert.IsType<ToolCallStep>(s.Trajectory[2]);
        Assert.Equal("get_order_status", tool.Call.ToolName);
        Assert.Equal("A1001", tool.Call.Arguments.GetProperty("order").GetString());
        Assert.Equal("shipped", tool.Result.Content);
        var intervention = Assert.IsType<SensorInterventionStep>(s.Trajectory[3]);
        Assert.Equal(HookPoint.PostModelCall, intervention.HookPoint);
        Assert.Equal("pii-redaction", intervention.SensorName);
        Assert.IsType<UserMessageStep>(intervention.TriggeringStep);

        // Forgot-a-field canary: full fidelity means re-serialising the restored checkpoint reproduces
        // the original JSON. Trips automatically if a newly added field doesn't survive the round-trip,
        // even when no one adds an explicit assertion for it above.
        Assert.Equal(json, CheckpointSerializer.Serialize(restored));
    }
}
