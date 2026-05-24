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
}
