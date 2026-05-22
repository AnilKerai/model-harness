using SapphireGuard.Framework.Sensors;
using SapphireGuard.Framework.Tools;

namespace SapphireGuard.Framework.State;

/// <summary>A single discrete event in an agent's trajectory.</summary>
public abstract record Step(Guid Id, DateTimeOffset Timestamp);

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
