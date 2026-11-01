using System.Text.Json;
using Anthropic.Models.Messages;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

using SdkMessage = Anthropic.Models.Messages.Message;
using SdkRole = Anthropic.Models.Messages.Role;
using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using StopReason = SapphireGuard.ModelHarness.Framework.State.StopReason;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudeWireFormatTests
{
    private static FrameworkMessage User(string text) => new(MessageRole.User, text);
    private static FrameworkMessage Assistant(string text) => new(MessageRole.Assistant, text);

    // The wire format HeadEvictionTrajectoryGuide renders a ToolCallStep into.
    private static FrameworkMessage ToolCallMessage(string callId, string name = "lookup") =>
        new(MessageRole.ToolUse, $"[tool_call name={name} id={callId}] {{\"q\":\"x\"}}");

    private static FrameworkMessage ToolResultMessage(string callId, string text = "ok", bool error = false) =>
        new(MessageRole.Tool, $"[tool_result id={callId} error={error}] {text}");

    // Real provider JSON through the SDK's own materialiser, so the tests exercise the deserialisation
    // path a live response takes rather than objects assembled by hand.
    private static SdkMessage Response(string json) =>
        SdkMessage.FromRawUnchecked(
            JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));

    private static string StringContent(MessageParam param)
    {
        Assert.True(param.Content.TryPickString(out var text), "expected plain string content");
        return text!;
    }

    private static IReadOnlyList<ContentBlockParam> Blocks(MessageParam param)
    {
        Assert.True(param.Content.TryPickContentBlockParams(out var blocks), "expected content blocks");
        return blocks!;
    }

    [Fact]
    public void TextOnlyTurn_IsSentAsAPlainStringNotABlockArray()
    {
        // The API accepts either, but a lone text block is sent as the plain string form.
        var messages = ClaudeWireFormat.BuildMessages([User("hello")]);

        var single = Assert.Single(messages);
        Assert.Equal(SdkRole.User, single.Role.Value());
        Assert.Equal("hello", StringContent(single));
    }

    [Fact]
    public void AssistantTextAndItsToolCall_ShareOneAssistantTurn_AndTheResultInlinesIntoAUserTurn()
    {
        // The API requires alternating roles, and a tool_use block must be followed by its tool_result
        // in the next user turn — so the call belongs on the assistant message, not beside it.
        var messages = ClaudeWireFormat.BuildMessages(
        [
            User("check the account"),
            Assistant("Checking the account first."),
            ToolCallMessage("c1"),
            ToolResultMessage("c1")
        ]);

        Assert.Equal(3, messages.Count);

        var assistant = messages[1];
        Assert.Equal(SdkRole.Assistant, assistant.Role.Value());
        var assistantBlocks = Blocks(assistant);
        Assert.Equal(2, assistantBlocks.Count);
        Assert.True(assistantBlocks[0].TryPickText(out var text));
        Assert.Equal("Checking the account first.", text!.Text);
        Assert.True(assistantBlocks[1].TryPickToolUse(out var toolUse));
        Assert.Equal("c1", toolUse!.ID);
        Assert.Equal("lookup", toolUse.Name);

        var toolTurn = messages[2];
        Assert.Equal(SdkRole.User, toolTurn.Role.Value());
        Assert.True(Assert.Single(Blocks(toolTurn)).TryPickToolResult(out var toolResult));
        Assert.Equal("c1", toolResult!.ToolUseID);
    }

    [Fact]
    public void ConsecutiveUserMessages_AreBatchedIntoOneTurn()
    {
        // Two user turns with no assistant reply between them would break the required alternation;
        // they are merged into one message carrying both text blocks.
        var messages = ClaudeWireFormat.BuildMessages([User("first"), User("second")]);

        var single = Assert.Single(messages);
        Assert.Equal(SdkRole.User, single.Role.Value());
        var blocks = Blocks(single);
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void SystemMessages_AreHoistedOutOfTheConversation_AndOnlyTheFirstIsForwarded()
    {
        // Anthropic takes the system prompt as a top-level request parameter, not a message role, so a
        // system message must never appear in Messages — and only one can be carried in System.
        var messages = new List<FrameworkMessage>
        {
            new(MessageRole.System, "You are a test agent."),
            User("hello"),
            new(MessageRole.System, "a later system message")
        };

        Assert.Equal("You are a test agent.", ClaudeWireFormat.SystemText(messages));

        var built = ClaudeWireFormat.BuildMessages(messages);
        Assert.Single(built);
        Assert.Equal(SdkRole.User, built[0].Role.Value());
    }

    [Fact]
    public void SystemText_WithNoSystemMessage_IsNull() =>
        Assert.Null(ClaudeWireFormat.SystemText([User("hello")]));

    [Fact]
    public void MapTool_CarriesNameDescriptionAndSchema()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");

        var tool = ClaudeWireFormat.MapTool(new ToolDefinition("lookup", "Look something up.", schema.RootElement.Clone()));

        Assert.Equal("lookup", tool.Name);
        Assert.Equal("Look something up.", tool.Description);
        Assert.NotNull(tool.InputSchema);
    }

    [Fact]
    public void ParseToolUse_WellFormed_ExtractsNameIdAndArgs()
    {
        var parsed = ClaudeWireFormat.ParseToolUse("[tool_call name=lookup id=c1] {\"q\":\"x\",\"n\":2}");

        Assert.Equal("c1", parsed.ID);
        Assert.Equal("lookup", parsed.Name);
        Assert.Equal("x", parsed.Input["q"].GetString());
        Assert.Equal(2, parsed.Input["n"].GetInt32());
    }

    [Theory]
    [InlineData("[tool_call name=lookup id=c1]")]                    // no argument text at all
    [InlineData("[tool_call name=lookup id=c1] {\"q\":\"x\"")]       // argument object never closed
    public void ParseToolUse_ArgsThatAreNotJson_Throws(string content)
    {
        // Unreachable from the pipeline — HeadEvictionTrajectoryGuide always writes the argument object,
        // and GetRawText() is at least "{}" — but pinned so the behaviour is a decision rather than an
        // accident. It is deliberately stricter than the empty-name/id fallback above: a marker that
        // does not parse still yields a block, but args that are not JSON cannot be built at all.
        Assert.ThrowsAny<JsonException>(() => ClaudeWireFormat.ParseToolUse(content));
    }

    [Theory]
    [InlineData("just some assistant text")]      // not a tool-call marker at all
    [InlineData("[tool_call name=lookup id=c1")]  // marker never closed
    [InlineData("")]
    public void ParseToolUse_NotAUsableMarker_YieldsAnEmptyNameAndId(string content)
    {
        // Pinned because it differs from the Azure adapter, which throws: the Anthropic SDK happily
        // builds a tool_use block with an empty id, so a malformed marker reaches the provider as a
        // call nothing can be matched to rather than failing loudly here.
        var parsed = ClaudeWireFormat.ParseToolUse(content);

        Assert.Equal("", parsed.ID);
        Assert.Equal("", parsed.Name);
    }

    [Fact]
    public void ParseToolResult_WellFormed_ExtractsIdErrorAndTrimmedText()
    {
        var parsed = ClaudeWireFormat.ParseToolResult("[tool_result id=c1 error=true]  boom");

        Assert.Equal("c1", parsed.ToolUseID);
        Assert.Equal("boom", parsed.Content);
        Assert.True(parsed.IsError);
    }

    [Fact]
    public void ParseToolResult_ErrorFalse_IsNotAnError()
    {
        var parsed = ClaudeWireFormat.ParseToolResult("[tool_result id=c1 error=False] ok");

        Assert.Equal("c1", parsed.ToolUseID);
        Assert.Equal("ok", parsed.Content);
        Assert.Null(parsed.IsError);
    }

    [Fact]
    public void ParseToolResult_NotAMarker_KeepsTheContentVerbatim()
    {
        var parsed = ClaudeWireFormat.ParseToolResult("plain result text");

        Assert.Equal("", parsed.ToolUseID);
        Assert.Equal("plain result text", parsed.Content);
    }

    [Fact]
    public void MapResponse_MergesTextBlocksAndMapsToolCalls()
    {
        var response = ClaudeWireFormat.MapResponse(Response("""
            {
              "id": "msg_01",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [
                { "type": "text", "text": "Checking." },
                { "type": "text", "text": "One moment." },
                { "type": "tool_use", "id": "toolu_1", "name": "lookup", "input": { "q": "x" } }
              ],
              "stop_reason": "tool_use",
              "stop_sequence": null,
              "usage": { "input_tokens": 100, "output_tokens": 20 }
            }
            """));

        Assert.Equal("Checking.\nOne moment.", response.Text);
        Assert.Equal(StopReason.ToolUse, response.StopReason);
        Assert.Equal("anthropic", response.Provider);
        Assert.Equal("claude-sonnet-4-5", response.Model);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_1", call.CallId);
        Assert.Equal("lookup", call.ToolName);
        Assert.Equal("x", call.Arguments.GetProperty("q").GetString());
    }

    [Fact]
    public void MapResponse_NoTextBlocks_TextIsNull()
    {
        // ModelResponse.Text is nullable precisely because a tool-only turn carries no prose.
        var response = ClaudeWireFormat.MapResponse(Response("""
            {
              "id": "msg_02",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [
                { "type": "tool_use", "id": "toolu_1", "name": "lookup", "input": {} }
              ],
              "stop_reason": "tool_use",
              "stop_sequence": null,
              "usage": { "input_tokens": 10, "output_tokens": 5 }
            }
            """));

        Assert.Null(response.Text);
    }

    [Fact]
    public void MapResponse_FoldsEveryCacheBucketIntoTheReportedInputTokens()
    {
        // Anthropic reports input_tokens as the uncached remainder only, so reporting it raw would
        // understate the prompt on every cached turn and let token budgets drift.
        var response = ClaudeWireFormat.MapResponse(Response("""
            {
              "id": "msg_03",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [ { "type": "text", "text": "ok" } ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 100,
                "output_tokens": 20,
                "cache_read_input_tokens": 300,
                "cache_creation_input_tokens": 50
              }
            }
            """));

        Assert.Equal(450, response.Usage.InputTokens);
        Assert.Equal(20, response.Usage.OutputTokens);
        Assert.Equal(300, response.CachedInputTokens);
        Assert.Equal(50, response.CacheWriteTokens);
    }

    [Fact]
    public void MapResponse_CountsCacheWritesButNotCacheReadsTowardTheRateLimit()
    {
        // Billing and rate limiting count different things: Anthropic excludes cache reads from ITPM,
        // so counting them here would throttle a well-cached agent ever earlier as the cache improves.
        var response = ClaudeWireFormat.MapResponse(Response("""
            {
              "id": "msg_04",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [ { "type": "text", "text": "ok" } ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 100,
                "output_tokens": 20,
                "cache_read_input_tokens": 300,
                "cache_creation_input_tokens": 50
              }
            }
            """));

        Assert.Equal(150, response.InputTokensTowardRateLimit);
    }

    [Fact]
    public void MapResponse_WithoutACache_ReportsTheWholePromptTowardTheRateLimit()
    {
        var response = ClaudeWireFormat.MapResponse(Response("""
            {
              "id": "msg_05",
              "type": "message",
              "role": "assistant",
              "model": "claude-sonnet-4-5",
              "content": [ { "type": "text", "text": "ok" } ],
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": { "input_tokens": 120, "output_tokens": 20 }
            }
            """));

        Assert.Equal(120, response.InputTokensTowardRateLimit);
    }

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
