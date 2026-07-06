namespace SapphireGuard.ModelHarness.Framework.Persistence;

/// <summary>
/// Persists and retrieves <see cref="Checkpoint"/> snapshots so a run can be resumed after
/// a crash or restart. The loop saves a checkpoint at the start of each turn; load the
/// latest checkpoint and pass its <see cref="Checkpoint.State"/> back to a fresh
/// <see cref="Agent"/> to resume from that point.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>Persists a checkpoint. Overwrites any existing checkpoint with the same <see cref="Checkpoint.CheckpointId"/>.</summary>
    Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default);

    /// <summary>Returns the checkpoint with the given ID, or <see langword="null"/> if not found.</summary>
    Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default);

    /// <summary>Returns the most recent checkpoint for the given task, or <see langword="null"/> if none exist.</summary>
    Task<Checkpoint?> LoadLatestAsync(string taskId, CancellationToken ct = default);

    /// <summary>
    /// Deletes all checkpoints for the given task. A no-op if none exist. Use for retention/cleanup
    /// and for data-erasure requests (GDPR/CCPA right-to-erasure) against the persisted trajectory.
    /// Not a default-interface no-op: a silently-unimplemented delete would make an erasure request
    /// appear to succeed while leaving the data on disk.
    /// </summary>
    Task DeleteAsync(string taskId, CancellationToken ct = default);
}
