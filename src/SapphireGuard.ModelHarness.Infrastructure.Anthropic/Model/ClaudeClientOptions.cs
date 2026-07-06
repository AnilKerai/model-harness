using System.Diagnostics.CodeAnalysis;
using AnthropicSdkOptions = Anthropic.Core.ClientOptions;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

/// <summary>Configuration for <see cref="ClaudeModelClient"/>.</summary>
[ExcludeFromCodeCoverage]
public sealed record ClaudeClientOptions
{
    public required string ApiKey { get; init; }

    /// <summary>
    /// Anthropic model identifier. Defaults to claude-sonnet-4-5.
    /// See https://docs.anthropic.com/en/docs/about-claude/models for current model IDs.
    /// </summary>
    public string ModelId { get; init; } = "claude-sonnet-4-5-20251001";

    /// <summary>
    /// Optional hook to configure the underlying SDK client — retry count, request timeout, base URL,
    /// custom headers, etc. The Anthropic SDK already retries transient failures and applies a request
    /// timeout by default; use this to tune them (e.g. <c>o =&gt; o.Timeout = TimeSpan.FromMinutes(2)</c>).
    /// </summary>
    public Action<AnthropicSdkOptions>? ConfigureClient { get; init; }
}
