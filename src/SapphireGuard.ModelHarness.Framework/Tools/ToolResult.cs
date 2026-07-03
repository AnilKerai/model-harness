using SapphireGuard.ModelHarness.Framework.State;

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
/// <param name="Cost">Optional cost incurred by the tool (e.g. a sub-agent delegation). Propagated to the parent budget.</param>
/// <param name="Usage">Optional token usage incurred by the tool (e.g. a sub-agent delegation). Propagated to the parent budget.</param>
/// <param name="Pins">
/// Optional reference content to pin into the persistent context region. The loop commits these to
/// <see cref="SapphireGuard.ModelHarness.Framework.State.AgentState.Pins"/> (replacing any pin with the
/// same label), where a guide renders them as non-evictable system sections — so a tool that loads a
/// procedure, contract, or spec can keep it verbatim in context past compaction. Default: none.
/// </param>
public sealed record ToolResult(
    string CallId,
    string Content,
    bool IsError = false,
    bool IsPending = false,
    decimal? Cost = null,
    Usage? Usage = null,
    IReadOnlyList<PinnedNote>? Pins = null);
