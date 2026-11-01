using System.Diagnostics.CodeAnalysis;
using AnthropicSdkOptions = Anthropic.Core.ClientOptions;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

/// <summary>Configuration for <see cref="ClaudeModelClient"/>.</summary>
[ExcludeFromCodeCoverage]
public sealed record ClaudeClientOptions
{
    public required string ApiKey { get; init; }

    /// <summary>
    /// Anthropic model identifier. Defaults to <c>claude-sonnet-4-5-20250929</c>, a dated snapshot of
    /// Claude Sonnet 4.5 (unlike an alias such as <c>claude-sonnet-4-5</c>, it always resolves to the same model).
    /// See https://docs.anthropic.com/en/docs/about-claude/models for current model IDs.
    /// </summary>
    public string ModelId { get; init; } = "claude-sonnet-4-5-20250929";

    /// <summary>
    /// Maximum number of output tokens the model may generate per call. The Anthropic API requires
    /// this value; when null it defaults to 8096. Raise it for tasks that need longer completions,
    /// lower it to cap per-call cost and latency.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Enables Anthropic prompt caching. Marks each request so its stable prefix — tool schemas,
    /// system prompt, and the prior-turn trajectory — is cached and re-read at 0.1× input cost or less
    /// (0.05× on Opus 5.5 and Sonnet 5.5, 0.025× on Fable 5.1) on later turns instead of re-billed in full. This is a
    /// win for the harness's turn-by-turn loop, where every turn shares a large prefix; the one-off
    /// cache-write premium (1.25×) is recovered by the second turn. Has no effect below the model's
    /// minimum cacheable prefix (512–4K tokens, model-dependent). Defaults to <see langword="true"/>; set <see langword="false"/> for
    /// single-shot calls that never reuse a prefix.
    /// </summary>
    public bool EnablePromptCaching { get; init; } = true;

    /// <summary>
    /// Optional hook to configure the underlying SDK client — retry count, request timeout, base URL,
    /// custom headers, OpenTelemetry, etc. The Anthropic SDK already retries transient failures and applies a
    /// request timeout by default; use this to tune them. The hook receives the options with
    /// <see cref="ApiKey"/> already set and must <b>return</b> the options to use. They are a struct, so
    /// editing the parameter would change a copy and nothing would apply; use a <c>with</c> expression:
    /// <c>o =&gt; o with { Timeout = TimeSpan.FromMinutes(2), MaxRetries = 1 }</c>. The SDK accepts at most one of
    /// <c>ApiKey</c>, <c>AuthToken</c> and <c>Credentials</c>, so to authenticate another way clear the key first:
    /// <c>o =&gt; o with { ApiKey = null, AuthToken = token }</c>.
    /// </summary>
    public Func<AnthropicSdkOptions, AnthropicSdkOptions>? ConfigureClient { get; init; }
}
