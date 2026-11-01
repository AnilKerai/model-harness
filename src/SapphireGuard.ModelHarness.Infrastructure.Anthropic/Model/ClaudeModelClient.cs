using System.Diagnostics.CodeAnalysis;
using Anthropic;
using Anthropic.Models.Messages;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using AnthropicSdkOptions = Anthropic.Core.ClientOptions;
using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

/// <summary>
/// <see cref="IModelClient"/> adapter over the official Anthropic SDK. Calls the Messages API and
/// delegates every framework ↔ SDK mapping to <see cref="ClaudeWireFormat"/>, which is where the
/// testable logic lives; this class is transport only.
/// </summary>
[ExcludeFromCodeCoverage]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Not IDisposable yet: wrapping the SDK client means nothing outside can dispose it, and implementing IDisposable is additive but needs a decision about directly-constructed instances first.")]
public sealed class ClaudeModelClient : IModelClient
{
    private readonly AnthropicClient _client;
    private readonly string _modelId;
    private readonly int _maxTokens;
    private readonly bool _enablePromptCaching;

    public ClaudeModelClient(ClaudeClientOptions options)
    {
        var clientOptions = new AnthropicSdkOptions { ApiKey = options.ApiKey };
        options.ConfigureClient?.Invoke(clientOptions);
        _client = new AnthropicClient(clientOptions);
        _modelId = options.ModelId;
        _maxTokens = options.MaxOutputTokens ?? 8096;
        _enablePromptCaching = options.EnablePromptCaching;
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<FrameworkMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var sdkMessages = ClaudeWireFormat.BuildMessages(messages);
        var sdkTools = availableTools.Select(ClaudeWireFormat.MapTool).Select(t => (ToolUnion)t).ToArray();
        var systemText = ClaudeWireFormat.SystemText(messages);

        var request = new MessageCreateParams
        {
            Model = _modelId,
            MaxTokens = _maxTokens,
            Messages = sdkMessages,
            Tools = sdkTools.Length > 0 ? sdkTools : null,
            System = systemText is null ? (MessageCreateParamsSystem?)null : systemText,
            // Top-level cache_control auto-places on the last cacheable block, so the tool + system +
            // prior-turn prefix is a cache read on the next turn rather than re-billed in full.
            CacheControl = _enablePromptCaching ? new CacheControlEphemeral() : null
        };

        var response = await _client.Messages.Create(request, ct);
        return ClaudeWireFormat.MapResponse(response);
    }
}
