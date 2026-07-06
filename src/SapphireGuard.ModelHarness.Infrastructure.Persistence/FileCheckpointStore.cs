using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

/// <summary>
/// Persists checkpoints as JSON files under <c>{baseDirectory}/{taskId}/</c>.
/// Files are named <c>{timestamp}_{checkpointId}.json</c> so lexicographic order
/// equals chronological order, making <see cref="LoadLatestAsync"/> a simple sort.
/// Because the task ID becomes a directory name, it must be a single path segment;
/// IDs that are empty or contain directory separators or <c>..</c> traversal are rejected.
/// </summary>
public sealed class FileCheckpointStore(string baseDirectory) : ICheckpointStore
{
    public async Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        var dir = TaskDirectory(checkpoint.State.TaskId);
        Directory.CreateDirectory(dir);

        var name = $"{checkpoint.CreatedAt:yyyyMMddHHmmssfff}_{checkpoint.CheckpointId}";
        var path = Path.Combine(dir, name + ".json");
        var tmp = Path.Combine(dir, name + ".tmp");
        var json = CheckpointSerializer.Serialize(checkpoint);

        // Write to a temp file then atomically rename, so a crash mid-write can't leave a torn
        // .json for LoadLatestAsync to trip over. The .tmp suffix keeps it out of the *.json glob.
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
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
        var dir = TaskDirectory(taskId);
        if (!Directory.Exists(dir))
            return null;

        // Newest first. Skip any file that's torn or corrupt (a crash mid-write can leave an
        // incomplete newest file) and fall back to the most recent intact checkpoint.
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderDescending())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                if (CheckpointSerializer.Deserialize(json) is { } checkpoint)
                    return checkpoint;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // corrupt or unreadable — try the next-newest
            }
        }

        return null;
    }

    public Task DeleteAsync(string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = TaskDirectory(taskId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    // Task IDs may be caller-supplied (AgentState.NewTask), so a hostile or careless value could
    // otherwise escape baseDirectory via separators, "..", or a rooted path. Resolving and requiring
    // the result to be a direct child of baseDirectory rejects all three in one cross-platform check.
    private string TaskDirectory(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID must be a non-empty string.", nameof(taskId));

        var baseFull = Path.GetFullPath(baseDirectory);
        var resolved = Path.GetFullPath(Path.Combine(baseFull, taskId));
        if (Path.GetDirectoryName(resolved) != baseFull)
            throw new ArgumentException(
                $"Task ID '{taskId}' must be a single path segment with no directory separators or '..' traversal.",
                nameof(taskId));

        return resolved;
    }
}
