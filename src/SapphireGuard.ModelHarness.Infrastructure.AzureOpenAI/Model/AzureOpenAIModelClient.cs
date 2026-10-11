using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using FrameworkUsage = SapphireGuard.ModelHarness.Framework.State.Usage;

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
    private readonly int? _maxOutputTokens;

    public AzureOpenAIModelClient(AzureOpenAIClientOptions options)
    {
        var sdkOptions = new Azure.AI.OpenAI.AzureOpenAIClientOptions();
        options.ConfigureClient?.Invoke(sdkOptions);

        AzureOpenAIClient client = options.ApiKey is not null
            ? new AzureOpenAIClient(options.Endpoint, new ApiKeyCredential(options.ApiKey), sdkOptions)
            : new AzureOpenAIClient(options.Endpoint, new DefaultAzureCredential(), sdkOptions);

        _chatClient = client.GetChatClient(options.DeploymentName);
        _deploymentName = options.DeploymentName;
        _maxOutputTokens = options.MaxOutputTokens;
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<FrameworkMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var chatMessages = AzureOpenAIWireFormat.BuildMessages(messages);

        var options = new ChatCompletionOptions();
        if (_maxOutputTokens is { } max)
            options.MaxOutputTokenCount = max;
        foreach (var tool in availableTools)
            options.Tools.Add(MapTool(tool));

        var result = await _chatClient.CompleteChatAsync(chatMessages, options, ct);
        return MapResponse(result.Value);
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

        var stopReason = AzureOpenAIWireFormat.MapStopReason(completion.FinishReason);
        var toolCalls = AzureOpenAIWireFormat.ParseToolCalls(completion.ToolCalls, stopReason);

        var usage = new FrameworkUsage(
            InputTokens: completion.Usage.InputTokenCount,
            OutputTokens: completion.Usage.OutputTokenCount);

        return new ModelResponse
        {
            Text = textParts.Count > 0 ? string.Join("\n", textParts) : null,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = usage,
            Cost = CalculateCost(_deploymentName, usage),
            Model = _deploymentName,
            Provider = "azure.ai.openai",
            // OpenAI's InputTokenCount already includes cached tokens (a subset), so no fold is needed —
            // surface the cached count for telemetry. There is no separate cache-write bucket on OpenAI.
            CachedInputTokens = completion.Usage.InputTokenDetails?.CachedTokenCount ?? 0
        };
    }

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
