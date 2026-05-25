namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>Outcome of a tool execution, surfaced back to the model as a tool result message.</summary>
public sealed record ToolResult(string CallId, string Content, bool IsError = false, bool IsPending = false)
{
    /// <summary>Matches the <see cref="ToolCall.CallId"/> this result responds to.</summary>
    public string CallId { get; } = CallId;

    /// <summary>The tool's output text.</summary>
    public string Content { get; } = Content;

    /// <summary>When <see langword="true"/>, the model sees this as a tool error rather than a successful result and can replan accordingly.</summary>
    public bool IsError { get; } = IsError;

    /// <summary>
    /// When <see langword="true"/>, the tool has signalled that it needs a human response before the run can continue.
    /// The harness suspends with <see cref="SapphireGuard.ModelHarness.Framework.State.AgentStatus.AwaitingHuman"/> and
    /// the caller must resume via <see cref="SapphireGuard.ModelHarness.Framework.State.AgentState.ResumeWithHumanAnswer"/>.
    /// </summary>
    public bool IsPending { get; } = IsPending;
}
