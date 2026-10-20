using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>A single discrete event in an agent's trajectory.</summary>
public abstract record Step(Guid Id, DateTimeOffset Timestamp);

/// <summary>
/// Records a user-authored message — the initial task or a conversational follow-up. The four
/// <c>*Baseline</c> properties snapshot the run's cumulative sensor and compaction spend when the
/// message was appended (<see cref="AgentState.WithUserMessage"/> fills them in), so a per-turn
/// budget can charge only what was spent since. They are zero for the opening message and for a
/// checkpoint written before they existed.
/// </summary>
public sealed record UserMessageStep(
    Guid Id,
    DateTimeOffset Timestamp,
    string Content) : Step(Id, Timestamp)
{
    /// <summary>Cumulative <see cref="AgentState.SensorUsage"/> when this message was appended.</summary>
    public Usage SensorUsageBaseline { get; init; } = Usage.Zero;

    /// <summary>Cumulative <see cref="AgentState.SensorCost"/> when this message was appended.</summary>
    public decimal SensorCostBaseline { get; init; }

    /// <summary>Cumulative <see cref="AgentState.CompactionUsage"/> when this message was appended.</summary>
    public Usage CompactionUsageBaseline { get; init; } = Usage.Zero;

    /// <summary>Cumulative <see cref="AgentState.CompactionCost"/> when this message was appended.</summary>
    public decimal CompactionCostBaseline { get; init; }
}

/// <summary>Records a call to the model and what came back.</summary>
public sealed record ModelCallStep(
    Guid Id,
    DateTimeOffset Timestamp,
    IReadOnlyList<Message> Prompt,
    ModelResponse Response,
    Usage Usage,
    decimal Cost) : Step(Id, Timestamp);

/// <summary>Records a tool invocation and its result.</summary>
public sealed record ToolCallStep(
    Guid Id,
    DateTimeOffset Timestamp,
    ToolCall Call,
    ToolResult Result) : Step(Id, Timestamp);

/// <summary>
/// Records a sensor blocking a transition. Rendered into the next prompt by
/// the context builder rather than living in tool-call history.
/// </summary>
public sealed record SensorInterventionStep(
    Guid Id,
    DateTimeOffset Timestamp,
    HookPoint HookPoint,
    string SensorName,
    string Reason,
    Step? TriggeringStep) : Step(Id, Timestamp);
