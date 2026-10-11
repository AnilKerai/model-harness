using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Ollama;

public sealed class OllamaWireFormatTests
{
    [Fact]
    public void ParseToolUse_WellFormed_ExtractsNameIdAndArgs()
    {
        var (name, callId, argsJson) = OllamaWireFormat.ParseToolUse(
            "[tool_call name=get_weather id=abc123] {\"city\":\"Paris\"}");

        Assert.Equal("get_weather", name);
        Assert.Equal("abc123", callId);
        Assert.Equal("{\"city\":\"Paris\"}", argsJson);
    }

    [Fact]
    public void ParseToolUse_NotAToolCall_ReturnsEmptyDefaults()
    {
        var (name, callId, argsJson) = OllamaWireFormat.ParseToolUse("just some assistant text");

        Assert.Equal("", name);
        Assert.Equal("", callId);
        Assert.Equal("{}", argsJson);
    }

    [Fact]
    public void ParseToolUse_MissingCloseBracket_ReturnsEmptyDefaults()
    {
        var (name, _, argsJson) = OllamaWireFormat.ParseToolUse("[tool_call name=x id=y");

        Assert.Equal("", name);
        Assert.Equal("{}", argsJson);
    }

    [Fact]
    public void ParseToolResult_WellFormed_ExtractsIdErrorAndTrimmedText()
    {
        var (callId, isError, text) = OllamaWireFormat.ParseToolResult(
            "[tool_result id=call-7 error=true]   boom");

        Assert.Equal("call-7", callId);
        Assert.True(isError);
        Assert.Equal("boom", text);
    }

    [Fact]
    public void ParseToolResult_ErrorFalse_IsNotAnError()
    {
        var (callId, isError, text) = OllamaWireFormat.ParseToolResult(
            "[tool_result id=c1 error=false] ok");

        Assert.Equal("c1", callId);
        Assert.False(isError);
        Assert.Equal("ok", text);
    }

    [Fact]
    public void ParseToolResult_NotAToolResult_ReturnsContentVerbatim()
    {
        var (callId, isError, text) = OllamaWireFormat.ParseToolResult("plain content");

        Assert.Equal("", callId);
        Assert.False(isError);
        Assert.Equal("plain content", text);
    }

    [Theory]
    [InlineData("stop", false, StopReason.EndTurn)]
    [InlineData("stop", true, StopReason.ToolUse)]       // Ollama reports "stop" even when tools were called
    [InlineData("length", false, StopReason.MaxTokens)]  // num_predict reached
    [InlineData("length", true, StopReason.MaxTokens)]   // truncated mid-call: must not read as ToolUse
    [InlineData(null, false, StopReason.EndTurn)]        // server predating done_reason: prior inference
    [InlineData(null, true, StopReason.ToolUse)]
    [InlineData("", false, StopReason.EndTurn)]
    public void MapStopReason_KnownAndMissingValues(string? doneReason, bool hasToolCalls, StopReason expected) =>
        Assert.Equal(expected, OllamaWireFormat.MapStopReason(doneReason, hasToolCalls));

    [Theory]
    [InlineData("load")]                 // lifecycle responses: an empty-messages load, or keep_alive 0
    [InlineData("unload")]
    [InlineData("a_reason_added_later")]
    public void MapStopReason_AnythingElse_IsOtherNeverEndTurn(string doneReason) =>
        Assert.Equal(StopReason.Other, OllamaWireFormat.MapStopReason(doneReason, hasToolCalls: false));
}
