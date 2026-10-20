using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.State;

public sealed class StopReasonTests
{
    // Checkpoints persist StopReason as an integer (CheckpointSerializer uses default enum handling), so a
    // renumbered member silently remaps every stored response. Members may be appended; values never change.
    [Theory]
    [InlineData(StopReason.EndTurn, 0)]
    [InlineData(StopReason.ToolUse, 1)]
    [InlineData(StopReason.MaxTokens, 2)]
    [InlineData(StopReason.StopSequence, 3)]
    [InlineData(StopReason.Other, 4)]
    [InlineData(StopReason.Refusal, 5)]
    [InlineData(StopReason.ContentFilter, 6)]
    [InlineData(StopReason.ContextWindowExceeded, 7)]
    public void Value_IsStable(StopReason reason, int expected) =>
        Assert.Equal(expected, (int)reason);

    [Fact]
    public void EveryMember_IsPinnedAbove() =>
        // Fails when a member is added, so whoever adds it also pins its value.
        Assert.Equal(8, Enum.GetValues<StopReason>().Length);

    [Fact]
    public void CheckpointSerializer_PersistsStopReasonAsAnInteger_AndRoundTrips()
    {
        // Guards the claim the two tests above depend on: if the serializer ever switched to string enums,
        // ordering would stop mattering and these pins would be protecting nothing.
        var step = new ModelCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, [],
            new ModelResponse
            {
                Text = "declined",
                ToolCalls = [],
                StopReason = StopReason.Refusal,
                Usage = Usage.Zero,
                Cost = 0m
            },
            Usage.Zero, 0m);

        var json = JsonSerializer.Serialize<Step>(step, CheckpointSerializer.Options);
        var back = (ModelCallStep)JsonSerializer.Deserialize<Step>(json, CheckpointSerializer.Options)!;

        Assert.Contains("\"StopReason\":5", json);
        Assert.Equal(StopReason.Refusal, back.Response.StopReason);
    }
}
