using ModelHarness.Framework.Sensors;
using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Tracing;

/// <summary>Base for structured trace events emitted by the loop.</summary>
public abstract record TraceEvent(string TaskId, DateTimeOffset Timestamp);

public sealed record TraceStarted(string TaskId, DateTimeOffset Timestamp, string TaskText) : TraceEvent(TaskId, Timestamp);

public sealed record ModelCallTrace(
    string TaskId,
    DateTimeOffset Timestamp,
    int PromptMessageCount,
    int ToolCount,
    ModelResponse Response) : TraceEvent(TaskId, Timestamp);

public sealed record ToolCallTrace(
    string TaskId,
    DateTimeOffset Timestamp,
    ToolCall Call,
    ToolResult Result,
    TimeSpan Duration) : TraceEvent(TaskId, Timestamp);

public sealed record SensorResultTrace(
    string TaskId,
    DateTimeOffset Timestamp,
    HookPoint HookPoint,
    string SensorName,
    SensorResult Result) : TraceEvent(TaskId, Timestamp);

public sealed record TraceCompleted(
    string TaskId,
    DateTimeOffset Timestamp,
    AgentStatus Status,
    string? FailureReason) : TraceEvent(TaskId, Timestamp);
