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
}
