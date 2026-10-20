using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>A single discrete event in an agent's trajectory.</summary>
public abstract record Step(Guid Id, DateTimeOffset Timestamp);

/// <summary>Records a user-authored message — the initial task or a conversational follow-up.</summary>
public sealed record UserMessageStep(
    Guid Id,
    DateTimeOffset Timestamp,
    string Content) : Step(Id, Timestamp);

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
    ToolResult Result) : Step(Id, Timestamp)
{
    /// <summary>
    /// <see langword="true"/> when the harness wrote <see cref="Result"/> itself rather than the tool —
    /// a call blocked at <see cref="HookPoint.PreToolCall"/>, one that hit the tool-call deadline, a call
    /// to a tool that was not offered on that turn, or a call not run because the run suspended for human
    /// input. Such a result carries no content from the tool's source, so it is not a taint. Set only by
    /// the loop; a tool cannot claim it, because a tool returns a <see cref="ToolResult"/>, never a step.
    /// Everything else, including a tool's error result or a message from an exception it threw, is the
    /// tool's output and <see langword="false"/> here. Checkpoints written before this property existed
    /// load as <see langword="false"/>, which errs towards treating the result as tainted.
    /// </summary>
    public bool HarnessAuthored { get; init; }
}

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
