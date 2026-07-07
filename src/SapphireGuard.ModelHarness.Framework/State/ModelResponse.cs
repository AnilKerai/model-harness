using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>A single response from <see cref="Model.IModelClient"/>.</summary>
public sealed record ModelResponse
{
    public required string? Text { get; init; }
    public required IReadOnlyList<ToolCall> ToolCalls { get; init; }
    public required StopReason StopReason { get; init; }
    public required Usage Usage { get; init; }
    public required decimal Cost { get; init; }

    /// <summary>Model id that served the response (e.g. <c>claude-opus-4</c>), surfaced as <c>gen_ai.request.model</c> in traces. Null when the client does not report it.</summary>
    public string? Model { get; init; }

    /// <summary>Provider that served the response (e.g. <c>anthropic</c>), surfaced as <c>gen_ai.provider.name</c> in traces. Null when the client does not report it.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Input tokens served from the provider's prompt cache (Anthropic <c>cache_read_input_tokens</c>,
    /// OpenAI <c>cached_tokens</c>) — a subset of the total input already counted in <see cref="Usage"/>,
    /// surfaced separately so cache hit-rate is observable in telemetry. Zero when caching is off or unsupported.
    /// </summary>
    public int CachedInputTokens { get; init; }

    /// <summary>
    /// Input tokens written to the provider's prompt cache this call (Anthropic <c>cache_creation_input_tokens</c>);
    /// providers that do not bill a separate cache write report 0. A subset of the total input in <see cref="Usage"/>.
    /// </summary>
    public int CacheWriteTokens { get; init; }
}
