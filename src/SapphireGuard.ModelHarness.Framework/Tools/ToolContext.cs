namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>Ambient context handed to a tool during execution.</summary>
/// <param name="TaskId">ID of the current task, matching <see cref="State.AgentState.TaskId"/>.</param>
/// <param name="CallId">ID of the tool call being executed, matching <see cref="ToolCall.CallId"/>.</param>
/// <param name="Metadata">Arbitrary key-value metadata forwarded from <see cref="State.AgentState.Metadata"/>.</param>
public sealed record ToolContext(string TaskId, string CallId, IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>Creates a <see cref="ToolContext"/> with empty metadata.</summary>
    public static ToolContext Empty(string taskId, string callId) =>
        new(taskId, callId, new Dictionary<string, string>());
}
