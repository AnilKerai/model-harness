using System.Globalization;
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
/// <param name="baseDirectory">Directory holding one subdirectory per task.</param>
/// <param name="retainPerTask">
/// How many of the newest checkpoints to keep per task, pruned after each successful save; older
/// files are deleted. <see langword="null"/> (the default) retains every checkpoint, so a task's
/// directory grows by one trajectory-sized file per turn. Setting 1 keeps resume working but gives
/// up the fallback past a corrupt newest file that <see cref="LoadLatestAsync"/> otherwise provides
/// — use 2 or more to keep that.
/// </param>
public sealed class FileCheckpointStore(string baseDirectory, int? retainPerTask = null) : ICheckpointStore
{
    private readonly int? _retainPerTask = retainPerTask is null || retainPerTask.Value > 0
        ? retainPerTask
        : throw new ArgumentOutOfRangeException(nameof(retainPerTask), retainPerTask, "Must be greater than zero, or null to retain every checkpoint.");

    public async Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default)
    {
        var dir = TaskDirectory(checkpoint.State.TaskId);
        Directory.CreateDirectory(dir);

        // Invariant culture, not the interpolation default (CurrentCulture): under a non-Gregorian
        // calendar locale (th-TH, ar-SA, fa-IR) the rendered year differs, which breaks the
        // lexicographic-order == chronological-order invariant LoadLatestAsync sorts on and lets a
        // stale checkpoint win the resume.
        var timestamp = checkpoint.CreatedAt.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var name = $"{timestamp}_{checkpoint.CheckpointId}";
        var path = Path.Combine(dir, name + ".json");
        var tmp = Path.Combine(dir, name + ".tmp");
        var json = CheckpointSerializer.Serialize(checkpoint);

        // Write to a temp file then atomically rename, so a crash mid-write can't leave a torn
        // .json for LoadLatestAsync to trip over. The .tmp suffix keeps it out of the *.json glob.
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);

        // Retention bounds the task directory, which otherwise grows by one trajectory-sized file per
        // turn (each checkpoint carries the whole trajectory; only ModelCallStep.Prompt is stripped).
        // It is a disk-bounding policy, not a correctness one: resume always reads the newest
        // checkpoint, and the file just renamed into place is the newest, so a prune cannot remove
        // what this save wrote.
        if (_retainPerTask is { } retain)
            PruneOldest(dir, retain);
    }

    // Only this store's own *.json names are candidates — a .tmp left by a killed process is not a
    // checkpoint and is left alone. A failed delete is swallowed rather than failing the save that
    // just succeeded: a concurrent reader or another process may hold the file, and losing a prune is
    // recoverable where a lost save is not.
    private static void PruneOldest(string dir, int retain)
    {
        var checkpoints = Directory.GetFiles(dir, "*.json");
        if (checkpoints.Length <= retain)
            return;

        foreach (var stale in checkpoints.OrderDescending().Skip(retain))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Held by a reader, or gone already — the next save prunes again.
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only or permission-denied — not worth failing a completed save over.
            }
        }
    }

    public async Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default)
    {
        ValidateCheckpointId(checkpointId);

        if (!Directory.Exists(baseDirectory))
            return null;

        foreach (var dir in Directory.GetDirectories(baseDirectory))
        {
            // Newest match first, skipping torn/corrupt files — the same hardening LoadLatestAsync
            // has, which this by-ID sibling was missing (it threw where the contract promises null).
            // Newest is also what makes SaveAsync's documented overwrite contract observable: the
            // filename carries CreatedAt, so re-saving one ID at a different time leaves two files,
            // and picking an arbitrary match could resurrect the superseded one.
            foreach (var file in Directory.GetFiles(dir, $"*_{checkpointId}.json").OrderDescending())
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
                    // corrupt or unreadable — try the next-newest match
                }
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

    // The checkpoint ID is interpolated into a search glob, so a wildcard would widen the match to
    // other checkpoints and a separator would reach outside the task directory. Framework-minted IDs
    // are GUIDs, but a caller may supply one, and it must not be able to change the query's shape.
    private static readonly char[] UnsafeIdChars = ['*', '?', '/', '\\', ':', '\0'];

    private static void ValidateCheckpointId(string checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId) || checkpointId.IndexOfAny(UnsafeIdChars) >= 0)
            throw new ArgumentException(
                $"Checkpoint ID '{checkpointId}' must be non-empty and contain no wildcards or directory separators.",
                nameof(checkpointId));
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
