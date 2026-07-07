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
    /// Maximum number of output tokens the model may generate per call. The Anthropic API requires
    /// this value; when null it defaults to 8096. Raise it for tasks that need longer completions,
    /// lower it to cap per-call cost and latency.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Enables Anthropic prompt caching. Marks each request so its stable prefix — tool schemas,
    /// system prompt, and the prior-turn trajectory — is cached and re-read at ~0.1× input cost on
    /// later turns instead of re-billed in full. This is a win for the harness's turn-by-turn loop,
    /// where every turn shares a large prefix; the one-off cache-write premium (1.25×) is recovered
    /// by the second turn. Has no effect below the model's minimum cacheable prefix (~1K–4K tokens,
    /// model-dependent). Defaults to <see langword="true"/>; set <see langword="false"/> for
    /// single-shot calls that never reuse a prefix.
    /// </summary>
    public bool EnablePromptCaching { get; init; } = true;

    /// <summary>
    /// Optional hook to configure the underlying SDK client — retry count, request timeout, base URL,
    /// custom headers, etc. The Anthropic SDK already retries transient failures and applies a request
    /// timeout by default; use this to tune them (e.g. <c>o =&gt; o.Timeout = TimeSpan.FromMinutes(2)</c>).
    /// </summary>
    public Action<AnthropicSdkOptions>? ConfigureClient { get; init; }
}
