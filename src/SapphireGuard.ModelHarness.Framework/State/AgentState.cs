using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// The complete, immutable state of an agent at a point in time. Every loop turn
/// produces a new <see cref="AgentState"/> via <c>with</c>-expressions; the trajectory
/// is the append-only log of state transitions.
/// </summary>
public sealed record AgentState
{
    /// <summary>Unique identifier for the task.</summary>
    public required string TaskId { get; init; }

    /// <summary>The original natural-language task text given to the agent.</summary>
    public required string TaskText { get; init; }

    /// <summary>The budget limits enforced during this run.</summary>
    public required Budget Budget { get; init; }

    /// <summary>Current lifecycle status of the run.</summary>
    public required AgentStatus Status { get; init; }

    /// <summary>Optional high-level plan the model has produced for this task.</summary>
    public string? Plan { get; init; }

    /// <summary>Optional working scratchpad the model uses for intermediate reasoning.</summary>
    public string? Scratchpad { get; init; }

    /// <summary>Ordered log of every <see cref="Step"/> taken during this run.</summary>
    public IReadOnlyList<Step> Trajectory { get; init; } = [];

    /// <summary>Arbitrary key-value metadata forwarded to tools via <see cref="SapphireGuard.ModelHarness.Framework.Tools.ToolContext"/>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Creates an initial state for a new task with a generated task ID and <see cref="AgentStatus.Running"/> status.</summary>
    public static AgentState NewTask(string taskText, Budget budget, IReadOnlyDictionary<string, string>? metadata = null) =>
        new()
        {
            TaskId = Guid.NewGuid().ToString("n"),
            TaskText = taskText,
            Budget = budget,
            Status = AgentStatus.Running,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

    /// <summary>Returns a new state with <paramref name="step"/> appended to the trajectory.</summary>
    public AgentState AppendStep(Step step) =>
        this with { Trajectory = [.. Trajectory, step] };

    /// <summary>
    /// Returns a new <see cref="AgentStatus.Running"/> state with the pending <c>ask_human</c>
    /// <see cref="ToolCallStep"/> (identified by <paramref name="callId"/>) replaced by a completed
    /// one carrying <paramref name="answer"/>. Pass the result back to <c>HarnessLoop.RunAsync</c>
    /// to continue the run from the point of suspension.
    /// </summary>
    public AgentState ResumeWithHumanAnswer(string callId, string answer)
    {
        var updated = new List<Step>(Trajectory.Count);
        foreach (var step in Trajectory)
        {
            if (step is ToolCallStep ts && ts.Result.IsPending && ts.Result.CallId == callId)
                updated.Add(ts with { Result = new ToolResult(callId, answer) });
            else
                updated.Add(step);
        }
        return this with { Trajectory = updated, Status = AgentStatus.Running };
    }
}
