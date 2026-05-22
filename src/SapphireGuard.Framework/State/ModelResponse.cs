using SapphireGuard.Framework.Tools;

namespace SapphireGuard.Framework.State;

/// <summary>A single response from <see cref="Model.IModelClient"/>.</summary>
public sealed record ModelResponse
{
    public required string? Text { get; init; }
    public required IReadOnlyList<ToolCall> ToolCalls { get; init; }
    public required StopReason StopReason { get; init; }
    public required Usage Usage { get; init; }
    public required decimal Cost { get; init; }
}
