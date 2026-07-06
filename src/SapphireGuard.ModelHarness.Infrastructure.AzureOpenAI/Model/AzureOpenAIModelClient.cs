using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using FrameworkUsage = SapphireGuard.ModelHarness.Framework.State.Usage;
using FrameworkStopReason = SapphireGuard.ModelHarness.Framework.State.StopReason;
using FrameworkToolCall = SapphireGuard.ModelHarness.Framework.Tools.ToolCall;

namespace SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;

/// <summary>
/// <see cref="IModelClient"/> adapter for Azure AI Foundry / Azure OpenAI Service.
/// Supports both API key and DefaultAzureCredential (managed identity) authentication.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class AzureOpenAIModelClient : IModelClient
{
    private readonly ChatClient _chatClient;
    private readonly string _deploymentName;

    public AzureOpenAIModelClient(AzureOpenAIClientOptions options)
    {
        var sdkOptions = new Azure.AI.OpenAI.AzureOpenAIClientOptions();
        options.ConfigureClient?.Invoke(sdkOptions);

        AzureOpenAIClient client = options.ApiKey is not null
            ? new AzureOpenAIClient(options.Endpoint, new ApiKeyCredential(options.ApiKey), sdkOptions)
            : new AzureOpenAIClient(options.Endpoint, new DefaultAzureCredential(), sdkOptions);

        _chatClient = client.GetChatClient(options.DeploymentName);
        _deploymentName = options.DeploymentName;
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<FrameworkMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var chatMessages = BuildMessages(messages);

        var options = new ChatCompletionOptions();
        foreach (var tool in availableTools)
            options.Tools.Add(MapTool(tool));

        var result = await _chatClient.CompleteChatAsync(chatMessages, options, ct);
        return MapResponse(result.Value);
    }

    private static List<ChatMessage> BuildMessages(IReadOnlyList<FrameworkMessage> messages)
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
                // Collect consecutive assistant/tool-use messages into a single AssistantChatMessage.
                // GPT models can return text alongside tool calls in one turn; both are batched here.
                string? text = null;
                var toolCalls = new List<ChatToolCall>();

                while (i < nonSystem.Count &&
                       nonSystem[i].Role is MessageRole.Assistant or MessageRole.ToolUse)
                {
                    if (nonSystem[i].Role == MessageRole.Assistant)
                        text = nonSystem[i].Content;
                    else
                        toolCalls.Add(ParseToolUse(nonSystem[i].Content));
                    i++;
                }

                result.Add(toolCalls.Count > 0
                    ? new AssistantChatMessage(toolCalls)
                    : new AssistantChatMessage(text ?? string.Empty));
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

    private static ChatTool MapTool(ToolDefinition def) =>
        ChatTool.CreateFunctionTool(def.Name, def.Description,
            BinaryData.FromString(def.InputSchema.GetRawText()));

    private ModelResponse MapResponse(ChatCompletion completion)
    {
        var textParts = completion.Content
            .Where(p => p.Kind == ChatMessageContentPartKind.Text)
            .Select(p => p.Text)
            .ToList();

        var toolCalls = completion.ToolCalls
            .Select(tc => new FrameworkToolCall(
                tc.Id,
                tc.FunctionName,
                JsonDocument.Parse(tc.FunctionArguments).RootElement))
            .ToList();

        var usage = new FrameworkUsage(
            InputTokens: completion.Usage.InputTokenCount,
            OutputTokens: completion.Usage.OutputTokenCount);

        return new ModelResponse
        {
            Text = textParts.Count > 0 ? string.Join("\n", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = MapStopReason(completion.FinishReason),
            Usage = usage,
            Cost = CalculateCost(_deploymentName, usage),
            Model = _deploymentName,
            Provider = "azure.ai.openai"
        };
    }

    private static FrameworkStopReason MapStopReason(ChatFinishReason? reason) => reason switch
    {
        ChatFinishReason r when r == ChatFinishReason.ToolCalls => FrameworkStopReason.ToolUse,
        ChatFinishReason r when r == ChatFinishReason.Length    => FrameworkStopReason.MaxTokens,
        _                                                       => FrameworkStopReason.EndTurn
    };

    // Approximate cost based on Azure AI Foundry published pricing (June 2026).
    // Inferred from deployment name — update from https://azure.microsoft.com/pricing/details/cognitive-services/openai-service/ when stale.
    private static decimal CalculateCost(string deploymentName, FrameworkUsage usage)
    {
        var name = deploymentName.ToLowerInvariant();
        var (inputPer1M, outputPer1M) = name switch
        {
            _ when name.Contains("gpt-4o-mini")  => (0.15m,  0.60m),
            _ when name.Contains("gpt-4.1-mini") => (0.40m,  1.60m),
            _ when name.Contains("gpt-4.1-nano") => (0.10m,  0.40m),
            _ when name.Contains("gpt-4.1")      => (2.00m,  8.00m),
            _ when name.Contains("o1-mini")      => (3.00m, 12.00m),
            _ when name.Contains("o1")
                || name.Contains("o3")           => (15.00m, 60.00m),
            _                                    => (2.50m, 10.00m)  // gpt-4o default
        };

        return (usage.InputTokens * inputPer1M / 1_000_000m)
             + (usage.OutputTokens * outputPer1M / 1_000_000m);
    }
}
