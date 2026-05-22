namespace SapphireGuard.ModelHarness.Framework.Persistence;

public interface ICheckpointStore
{
    Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default);
    Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default);
    Task<Checkpoint?> LoadLatestAsync(string taskId, CancellationToken ct = default);
}
