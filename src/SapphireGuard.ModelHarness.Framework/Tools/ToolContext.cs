namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>Ambient context handed to a tool during execution.</summary>
public sealed record ToolContext(string TaskId, string CallId, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>ID of the current task, matching <see cref="State.AgentState.TaskId"/>.</summary>
    public string TaskId { get; } = TaskId;

    /// <summary>ID of the tool call being executed, matching <see cref="ToolCall.CallId"/>.</summary>
    public string CallId { get; } = CallId;

    /// <summary>Arbitrary key-value metadata forwarded from <see cref="State.AgentState.Metadata"/>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; } = Metadata;

    /// <summary>Creates a <see cref="ToolContext"/> with empty metadata.</summary>
    public static ToolContext Empty(string taskId, string callId) =>
        new(taskId, callId, new Dictionary<string, string>());
}
