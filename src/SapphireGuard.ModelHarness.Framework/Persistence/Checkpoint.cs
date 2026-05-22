using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Persistence;

public sealed record Checkpoint
{
    public required string CheckpointId { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int TurnNumber { get; init; }
    public required AgentState State { get; init; }
}
