using System.Text.Json;
using OpenAI.Chat;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;

// Pure mapping from the harness's rendered messages to chat-completions messages (mirrors
// OllamaWireFormat). Kept out of the I/O client so the batching and parsing — the adapter's only
// branching logic — is unit-testable and counts toward coverage.
internal static class AzureOpenAIWireFormat
{
    public static List<ChatMessage> BuildMessages(IReadOnlyList<Message> messages)
    {
        var result = new List<ChatMessage>();

        var systemText = messages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content)
            .FirstOrDefault();

        if (systemText is not null)
            result.Add(new SystemChatMessage(systemText));

        var nonSystem = messages.Where(m => m.Role != MessageRole.System).ToList();

        int i = 0;
        while (i < nonSystem.Count)
        {
            var msg = nonSystem[i];

            if (msg.Role == MessageRole.User)
            {
                result.Add(new UserChatMessage(msg.Content));
                i++;
            }
            else if (msg.Role is MessageRole.Assistant or MessageRole.ToolUse)
            {
                // Collect consecutive assistant/tool-use messages into a single AssistantChatMessage:
                // chat-completions providers differ in how they treat back-to-back assistant messages,
                // so the run is sent as one. Every text in it is kept — a harness observation following
                // an answer must not replace the answer, and text beside a tool call rides on the same
                // message rather than being dropped.
                var texts = new List<string>();
                var toolCalls = new List<ChatToolCall>();

                while (i < nonSystem.Count &&
                       nonSystem[i].Role is MessageRole.Assistant or MessageRole.ToolUse)
                {
                    if (nonSystem[i].Role == MessageRole.Assistant)
                        texts.Add(nonSystem[i].Content);
                    else
                        toolCalls.Add(ParseToolUse(nonSystem[i].Content));
                    i++;
                }

                var text = string.Join("\n\n", texts);
                if (toolCalls.Count == 0)
                {
                    result.Add(new AssistantChatMessage(text));
                }
                else
                {
                    var assistant = new AssistantChatMessage(toolCalls);
                    if (text.Length > 0)
                        assistant.Content.Add(ChatMessageContentPart.CreateTextPart(text));
                    result.Add(assistant);
                }
            }
            else if (msg.Role == MessageRole.Tool)
            {
                var (callId, resultText) = ParseToolResult(msg.Content);
                result.Add(new ToolChatMessage(callId, resultText));
                i++;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static ChatToolCall ParseToolUse(string content)
    {
        // Format written by HeadEvictionTrajectoryGuide: "[tool_call name={name} id={callId}] {argsJson}"
        string name = "";
        string callId = "";
        string argsJson = "{}";

        if (content.StartsWith("[tool_call ", StringComparison.Ordinal))
        {
            var closeBracket = content.IndexOf(']');
            if (closeBracket > 0)
            {
                var meta = content[1..closeBracket];
                argsJson = content[(closeBracket + 1)..].TrimStart();
                foreach (var part in meta.Split(' '))
                {
                    if (part.StartsWith("name=", StringComparison.Ordinal)) name = part[5..];
                    if (part.StartsWith("id=", StringComparison.Ordinal)) callId = part[3..];
                }
            }
        }

        return ChatToolCall.CreateFunctionToolCall(callId, name, BinaryData.FromString(argsJson));
    }

    private static (string CallId, string ResultText) ParseToolResult(string content)
    {
        // Format written by HeadEvictionTrajectoryGuide: "[tool_result id={callId} error={bool}] {resultText}"
        string callId = "";
        string resultText = content;

        if (content.StartsWith("[tool_result ", StringComparison.Ordinal))
        {
            var closeBracket = content.IndexOf(']');
            if (closeBracket > 0)
            {
                var meta = content[1..closeBracket];
                resultText = content[(closeBracket + 1)..].TrimStart();
                foreach (var part in meta.Split(' '))
                {
                    if (part.StartsWith("id=", StringComparison.Ordinal)) callId = part[3..];
                }
            }
        }

        return (callId, resultText);
    }

    // Every reason the provider reports maps explicitly; anything else is Other, never EndTurn, so the loop's
    // allowlist keeps it out of Done. FunctionCall is obsolete (this adapter only declares tools) and falls to Other.
    public static StopReason MapStopReason(ChatFinishReason? reason) => reason switch
    {
        ChatFinishReason.Stop => StopReason.EndTurn,
        ChatFinishReason.ToolCalls => StopReason.ToolUse,
        ChatFinishReason.Length => StopReason.MaxTokens,
        ChatFinishReason.ContentFilter => StopReason.ContentFilter,
        _ => StopReason.Other
    };

    // A response cut short at Length or by the content filter can carry tool-call arguments that are truncated
    // JSON. The loop never dispatches calls from such a response, so an unparseable one is dropped rather than
    // failing the run with a parser error before the loop can report why the response stopped. On any other
    // response a malformed argument is a provider fault, and still throws.
    public static List<ToolCall> ParseToolCalls(IEnumerable<ChatToolCall> toolCalls, StopReason stopReason)
    {
        var parsed = new List<ToolCall>();
        foreach (var call in toolCalls)
        {
            try
            {
                parsed.Add(new ToolCall(call.Id, call.FunctionName, JsonDocument.Parse(call.FunctionArguments).RootElement));
            }
            catch (JsonException) when (stopReason is StopReason.MaxTokens or StopReason.ContentFilter)
            {
                // Dropped: a partial call from a response the loop will report as cut short.
            }
        }
        return parsed;
    }
}
