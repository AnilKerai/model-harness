namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>Outcome of a tool execution, surfaced back to the model as a tool result message.</summary>
/// <param name="CallId">Matches the <see cref="ToolCall.CallId"/> this result responds to.</param>
/// <param name="Content">The tool's output text.</param>
/// <param name="IsError">When <see langword="true"/>, the model sees this as a tool error rather than a successful result and can replan accordingly.</param>
/// <param name="IsPending">
/// When <see langword="true"/>, the tool has signalled that it needs a human response before the run can continue.
/// The harness suspends with <see cref="SapphireGuard.ModelHarness.Framework.State.AgentStatus.AwaitingHuman"/> and
/// the caller must resume via <see cref="SapphireGuard.ModelHarness.Framework.State.AgentState.ResumeWithHumanAnswer"/>.
/// </param>
public sealed record ToolResult(string CallId, string Content, bool IsError = false, bool IsPending = false);
