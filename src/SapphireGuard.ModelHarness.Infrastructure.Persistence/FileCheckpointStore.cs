using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

/// <summary>
/// Persists checkpoints as JSON files under <c>{baseDirectory}/{taskId}/</c>.
/// Files are named <c>{timestamp}_{checkpointId}.json</c> so lexicographic order
/// equals chronological order, making <see cref="LoadLatestAsync"/> a simple sort.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FileCheckpointStore(string baseDirectory) : ICheckpointStore
{
    public async Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        var dir = Path.Combine(baseDirectory, checkpoint.State.TaskId);
        Directory.CreateDirectory(dir);

        var filename = $"{checkpoint.CreatedAt:yyyyMMddHHmmssfff}_{checkpoint.CheckpointId}.json";
        var path = Path.Combine(dir, filename);
        var json = CheckpointSerializer.Serialize(checkpoint);

        await File.WriteAllTextAsync(path, json, ct);
    }

    public async Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default)
    {
        if (!Directory.Exists(baseDirectory))
            return null;

        foreach (var dir in Directory.GetDirectories(baseDirectory))
        {
            var matches = Directory.GetFiles(dir, $"*_{checkpointId}.json");
            if (matches.Length > 0)
            {
                var json = await File.ReadAllTextAsync(matches[0], ct);
                return CheckpointSerializer.Deserialize(json);
            }
        }

        return null;
    }

    public async Task<Checkpoint?> LoadLatestAsync(string taskId, CancellationToken ct = default)
    {
        var dir = Path.Combine(baseDirectory, taskId);
        if (!Directory.Exists(dir))
            return null;

        var files = Directory.GetFiles(dir, "*.json")
            .OrderDescending()
            .ToArray();

        if (files.Length == 0)
            return null;

        var json = await File.ReadAllTextAsync(files[0], ct);
        return CheckpointSerializer.Deserialize(json);
    }
}
