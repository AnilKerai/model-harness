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
}
