using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using SdkMessage = Anthropic.Models.Messages.Message;
using SdkRole = Anthropic.Models.Messages.Role;
using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using FrameworkUsage = SapphireGuard.ModelHarness.Framework.State.Usage;
using FrameworkStopReason = SapphireGuard.ModelHarness.Framework.State.StopReason;
using FrameworkToolCall = SapphireGuard.ModelHarness.Framework.Tools.ToolCall;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

/// <summary>
/// <see cref="IModelClient"/> adapter over the official Anthropic SDK.
/// Maps framework messages and tool definitions to SDK types, calls the
/// Messages API, and maps the response back to framework types.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ClaudeModelClient : IModelClient
{
    private readonly AnthropicClient _client;
    private readonly string _modelId;

    public ClaudeModelClient(ClaudeClientOptions options)
    {
        _client = new AnthropicClient(new global::Anthropic.Core.ClientOptions
        {
            ApiKey = options.ApiKey
        });
        _modelId = options.ModelId;
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<FrameworkMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var systemText = messages
            .Where(m => m.Role == MessageRole.System)
            .Select(m => m.Content)
            .FirstOrDefault();

        var sdkMessages = BuildMessages(messages);
        var sdkTools = availableTools.Select(MapTool).Select(t => (ToolUnion)t).ToArray();

        var request = new MessageCreateParams
        {
            Model = _modelId,
            MaxTokens = 8096,
            Messages = sdkMessages,
            Tools = sdkTools.Length > 0 ? sdkTools : null,
            System = systemText is null ? (MessageCreateParamsSystem?)null : systemText
        };

        var response = await _client.Messages.Create(request, ct);
        return MapResponse(response);
    }

    private static List<MessageParam> BuildMessages(IReadOnlyList<FrameworkMessage> messages)
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

    private static ToolUseBlockParam ParseToolUse(string content)
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

    private static ToolResultBlockParam ParseToolResult(string content)
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

    private static Tool MapTool(ToolDefinition def)
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

    private static ModelResponse MapResponse(SdkMessage response)
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

        var usage = new FrameworkUsage(
            InputTokens: (int)response.Usage.InputTokens,
            OutputTokens: (int)response.Usage.OutputTokens);

        var stopReason = MapStopReason(response.StopReason?.Raw());
        var cost = CalculateCost(ExtractModelFamily(response.Model), usage);

        return new ModelResponse
        {
            Text = textParts.Count > 0 ? string.Join("\n", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = usage,
            Cost = cost
        };
    }

    private static FrameworkStopReason MapStopReason(string? raw) => raw switch
    {
        "tool_use" => FrameworkStopReason.ToolUse,
        "max_tokens" => FrameworkStopReason.MaxTokens,
        "stop_sequence" => FrameworkStopReason.StopSequence,
        _ => FrameworkStopReason.EndTurn
    };

    /// <summary>
    /// Approximate cost based on published pricing (May 2026).
    /// Prices change — update from https://www.anthropic.com/pricing when stale.
    /// </summary>
    private static decimal CalculateCost(string modelFamily, FrameworkUsage usage)
    {
        var (inputPer1M, outputPer1M) = modelFamily switch
        {
            "haiku" => (0.80m, 4.00m),
            "opus" => (15.00m, 75.00m),
            _ => (3.00m, 15.00m) // sonnet default
        };

        return (usage.InputTokens * inputPer1M / 1_000_000m)
             + (usage.OutputTokens * outputPer1M / 1_000_000m);
    }

    private static string ExtractModelFamily(string? modelId)
    {
        if (modelId is null) return "sonnet";
        if (modelId.Contains("haiku", StringComparison.OrdinalIgnoreCase)) return "haiku";
        if (modelId.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        return "sonnet";
    }
}
