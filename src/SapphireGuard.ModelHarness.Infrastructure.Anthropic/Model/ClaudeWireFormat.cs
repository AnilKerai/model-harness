using System.Text.Json;
using Anthropic.Models.Messages;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using SdkMessage = Anthropic.Models.Messages.Message;
using SdkRole = Anthropic.Models.Messages.Role;
using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using FrameworkUsage = SapphireGuard.ModelHarness.Framework.State.Usage;
using FrameworkToolCall = SapphireGuard.ModelHarness.Framework.Tools.ToolCall;

// Anthropic.Models.Messages also declares a StopReason; this file means the harness's enum throughout.
using StopReason = SapphireGuard.ModelHarness.Framework.State.StopReason;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

// Pure mappings between framework types and the Anthropic Messages wire format. Split out of
// ClaudeModelClient so every mapping is unit-testable without an HTTP client — the same arrangement
// AzureOpenAIWireFormat gives the Azure adapter. The client keeps only transport: build the request,
// call the API, map the response.
internal static class ClaudeWireFormat
{
    // Values from https://docs.claude.com/en/api/handling-stop-reasons. Anything unlisted maps to Other, never
    // EndTurn, so the loop's allowlist keeps it out of Done. pause_turn only follows a server-tool loop, and this
    // adapter declares client tools only, so it cannot arrive today; if it ever does, the response is incomplete
    // by definition and Other is correct.
    public static StopReason MapStopReason(string? raw) => raw switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        "model_context_window_exceeded" => StopReason.ContextWindowExceeded,
        _ => StopReason.Other
    };

    // Anthropic carries the system prompt as a top-level request parameter rather than a message role, so
    // exactly one can be forwarded and every later system message is dropped. That is safe because the
    // harness keeps "one System message, and it comes first": DefaultContextBuilder folds the rolling
    // summary and the goal anchors into that single message.
    public static string? SystemText(IReadOnlyList<FrameworkMessage> messages) =>
        messages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content)
            .FirstOrDefault();

    public static List<MessageParam> BuildMessages(IReadOnlyList<FrameworkMessage> messages)
    {
        var result = new List<MessageParam>();

        // System messages are hoisted to MessageCreateParams.System
        var nonSystem = messages.Where(m => m.Role != MessageRole.System).ToList();

        // The Anthropic API requires alternating user/assistant turns. We batch
        // consecutive same-role messages together using content blocks, and inline
        // tool results (our Tool role) into user turns.
        var pendingBlocks = new List<ContentBlockParam>();
        SdkRole? pendingRole = null;

        foreach (var msg in nonSystem)
        {
            var sdkRole = msg.Role is MessageRole.Assistant or MessageRole.ToolUse
                ? SdkRole.Assistant
                : SdkRole.User;

            if (pendingRole is not null && pendingRole != sdkRole)
            {
                Flush(result, pendingRole.Value, pendingBlocks);
                pendingBlocks.Clear();
            }

            pendingRole = sdkRole;

            if (msg.Role == MessageRole.Tool)
            {
                pendingBlocks.Add(new ContentBlockParam(ParseToolResult(msg.Content)));
            }
            else if (msg.Role == MessageRole.ToolUse)
            {
                pendingBlocks.Add(new ContentBlockParam(ParseToolUse(msg.Content)));
            }
            else
            {
                pendingBlocks.Add(new ContentBlockParam(new TextBlockParam { Text = msg.Content }));
            }
        }

        if (pendingBlocks.Count > 0 && pendingRole is not null)
        {
            Flush(result, pendingRole.Value, pendingBlocks);
        }

        return result;
    }

    private static void Flush(List<MessageParam> result, SdkRole role, List<ContentBlockParam> blocks)
    {
        if (blocks.Count == 1 && blocks[0].TryPickText(out var textBlock))
        {
            result.Add(new MessageParam { Role = role, Content = textBlock.Text });
        }
        else
        {
            result.Add(new MessageParam { Role = role, Content = new MessageParamContent([.. blocks]) });
        }
    }

    // A marker that does not parse yields empty name/id rather than throwing: the SDK still builds a
    // tool_use block, so a malformed marker reaches the provider as an unmatchable call. (The Azure
    // adapter throws here, because its SDK refuses to build the message at all.)
    public static ToolUseBlockParam ParseToolUse(string content)
    {
        // Content format written by HeadEvictionTrajectoryGuide:
        // "[tool_call name={name} id={callId}] {argsJson}"
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
                    if (part.StartsWith("name=", StringComparison.Ordinal))
                        name = part[5..];
                    if (part.StartsWith("id=", StringComparison.Ordinal))
                        callId = part[3..];
                }
            }
        }

        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson) ?? [];
        return new ToolUseBlockParam { ID = callId, Name = name, Input = input };
    }

    // As with ParseToolUse, an unparseable marker yields empty id / verbatim text rather than throwing.
    public static ToolResultBlockParam ParseToolResult(string content)
    {
        // Content format written by HeadEvictionTrajectoryGuide:
        // "[tool_result id={callId} error={bool}] {resultText}"
        string callId = "";
        bool isError = false;
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
                    if (part.StartsWith("id=", StringComparison.Ordinal))
                        callId = part[3..];
                    if (part.StartsWith("error=", StringComparison.Ordinal))
                        isError = string.Equals(part[6..], "true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return new ToolResultBlockParam
        {
            ToolUseID = callId,
            Content = resultText,
            IsError = isError ? true : null
        };
    }

    public static Tool MapTool(ToolDefinition def)
    {
        var schemaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            def.InputSchema.GetRawText()) ?? [];

        return new Tool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = InputSchema.FromRawUnchecked(schemaDict)
        };
    }

    public static ModelResponse MapResponse(SdkMessage response)
    {
        var textParts = new List<string>();
        var toolCalls = new List<FrameworkToolCall>();

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var tb))
            {
                textParts.Add(tb.Text);
            }
            else if (block.TryPickToolUse(out var tu))
            {
                var argsJson = JsonSerializer.Serialize(tu.Input);
                var argsElement = JsonDocument.Parse(argsJson).RootElement;
                toolCalls.Add(new FrameworkToolCall(tu.ID, tu.Name, argsElement));
            }
        }

        // With prompt caching on, Anthropic reports input_tokens as the uncached remainder only;
        // the cached prefix is billed separately (reads at a per-model fraction of input, 5-min writes
        // at 1.25x). Fold all three buckets into the reported input count so token budgets see the
        // true prompt size, and price each bucket at its own rate below.
        var uncachedInput = response.Usage.InputTokens;
        var cacheReadTokens = response.Usage.CacheReadInputTokens ?? 0;
        var cacheWriteTokens = response.Usage.CacheCreationInputTokens ?? 0;
        var outputTokens = response.Usage.OutputTokens;

        var usage = new FrameworkUsage(
            InputTokens: (int)(uncachedInput + cacheReadTokens + cacheWriteTokens),
            OutputTokens: (int)outputTokens);

        // Rate limiting counts a different number than billing does: Anthropic excludes cache reads from
        // ITPM, so caching raises effective throughput rather than consuming it (their worked example: a
        // 2M ITPM limit with an 80% hit rate carries 10M total input tokens/min). Counting the folded
        // input here would throttle a well-cached agent several times too early, and get worse the better
        // the cache performed. Cache *writes* do count, so they stay in.
        // The one exception is Haiku 3.5, which counts cache reads too — it is retired on this API, so it
        // cannot arrive through this client; revisit if a Bedrock/Vertex backend is ever added.
        var rateLimitedInput = (int)(uncachedInput + cacheWriteTokens);

        var stopReason = MapStopReason(response.StopReason?.Raw());
        var cost = ClaudePricing.Cost(response.Model, uncachedInput, cacheReadTokens, cacheWriteTokens, outputTokens);

        return new ModelResponse
        {
            Text = textParts.Count > 0 ? string.Join("\n", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = usage,
            Cost = cost,
            Model = response.Model,
            Provider = "anthropic",
            CachedInputTokens = (int)cacheReadTokens,
            CacheWriteTokens = (int)cacheWriteTokens,
            InputTokensTowardRateLimit = rateLimitedInput
        };
    }
}
