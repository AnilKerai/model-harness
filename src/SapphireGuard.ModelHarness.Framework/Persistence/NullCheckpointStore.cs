using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Persistence;

[ExcludeFromCodeCoverage]
public sealed class NullCheckpointStore : ICheckpointStore
{
    public Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default) => Task.FromResult<Checkpoint?>(null);
    public Task<Checkpoint?> LoadLatestAsync(string taskId, CancellationToken ct = default) => Task.FromResult<Checkpoint?>(null);
    public Task DeleteAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;
}
