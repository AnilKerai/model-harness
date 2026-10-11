using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudeWireFormatTests
{
    [Theory]
    [InlineData("end_turn", StopReason.EndTurn)]
    [InlineData("tool_use", StopReason.ToolUse)]
    [InlineData("max_tokens", StopReason.MaxTokens)]
    [InlineData("stop_sequence", StopReason.StopSequence)]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("model_context_window_exceeded", StopReason.ContextWindowExceeded)]
    public void MapStopReason_DocumentedReasons(string raw, StopReason expected) =>
        Assert.Equal(expected, ClaudeWireFormat.MapStopReason(raw));

    [Theory]
    [InlineData("pause_turn")]          // server-tool loops only; this adapter declares client tools
    [InlineData("a_reason_added_later")]
    [InlineData(null)]
    public void MapStopReason_AnythingElse_IsOtherNeverEndTurn(string? raw) =>
        Assert.Equal(StopReason.Other, ClaudeWireFormat.MapStopReason(raw));
}
