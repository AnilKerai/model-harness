namespace SapphireGuard.Framework.State;

/// <summary>
/// The complete, immutable state of an agent at a point in time. Every loop turn
/// produces a new <see cref="AgentState"/> via <c>with</c>-expressions; the trajectory
/// is the durable log of state transitions.
/// </summary>
public sealed record AgentState
{
    public required string TaskId { get; init; }
    public required string TaskText { get; init; }
    public required Budget Budget { get; init; }
    public required AgentStatus Status { get; init; }

    public string? Plan { get; init; }
    public string? Scratchpad { get; init; }
    public IReadOnlyList<Step> Trajectory { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public static AgentState NewTask(string taskText, Budget budget, IReadOnlyDictionary<string, string>? metadata = null) =>
        new()
        {
            TaskId = Guid.NewGuid().ToString("n"),
            TaskText = taskText,
            Budget = budget,
            Status = AgentStatus.Running,
            Metadata = metadata ?? new Dictionary<string, string>()
        };

    public AgentState AppendStep(Step step) =>
        this with { Trajectory = [.. Trajectory, step] };
}
