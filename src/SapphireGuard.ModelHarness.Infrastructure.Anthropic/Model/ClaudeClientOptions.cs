namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

/// <summary>Configuration for <see cref="ClaudeModelClient"/>.</summary>
public sealed record ClaudeClientOptions
{
    public required string ApiKey { get; init; }

    /// <summary>
    /// Anthropic model identifier. Defaults to claude-sonnet-4-5.
    /// See https://docs.anthropic.com/en/docs/about-claude/models for current model IDs.
    /// </summary>
    public string ModelId { get; init; } = "claude-sonnet-4-5-20251001";
}
