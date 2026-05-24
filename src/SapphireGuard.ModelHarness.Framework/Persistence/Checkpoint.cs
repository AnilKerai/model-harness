using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Persistence;

/// <summary>A durable snapshot of <see cref="AgentState"/> saved at the start of each loop turn.</summary>
public sealed record Checkpoint
{
    /// <summary>Unique identifier for this individual checkpoint.</summary>
    public required string CheckpointId { get; init; }

    /// <summary>Identifies the run; all checkpoints for the same task share this value.</summary>
    public required string RunId { get; init; }

    /// <summary>When this checkpoint was saved.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The loop turn index at which this checkpoint was created.</summary>
    public required int TurnNumber { get; init; }

    /// <summary>The full agent state at this checkpoint, including trajectory.</summary>
    public required AgentState State { get; init; }
}
