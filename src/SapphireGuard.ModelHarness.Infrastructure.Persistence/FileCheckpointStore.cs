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
public sealed class FileCheckpointStore(string baseDirectory, int? retainPerTask) : ICheckpointStore
{
    // A real overload, not a default on the parameter above: an optional parameter changes the
    // constructor's signature, so callers already compiled against the one-argument form would fail
    // with MissingMethodException after an upgrade. Both signatures have to exist.
    public FileCheckpointStore(string baseDirectory) : this(baseDirectory, null) { }

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
        // It is a disk-bounding policy, not a correctness one: resume always reads the newest checkpoint,
        // and the prune is told which path it just wrote so it can never remove it.
        if (_retainPerTask is { } retain)
            PruneOldest(dir, path, retain);
    }

    // The file just renamed into place is excluded from the candidates, not assumed to be the newest by
    // ordering. Ordering is by filename, and the filename's timestamp is only millisecond-resolution, so
    // two saves in the same millisecond — a frozen test clock, a fast host — tie-break on the random
    // checkpoint id, and a clock that steps backwards puts the newest write last. Either way the newest
    // state could be pruned, on the very path that matters most: HarnessLoop saves at the top of each
    // turn and again when a run suspends for human input (HarnessLoop.cs SuspendForHuman), so losing that
    // save loses the resumable state. Excluding it makes "retention never removes what it just wrote"
    // true by construction rather than by argument.
    //
    // Only this store's own *.json names are candidates — a .tmp left by a killed process is not a
    // checkpoint and is left alone. A failed delete is swallowed rather than failing the save that just
    // succeeded: a concurrent reader or another process may hold the file, and losing a prune is
    // recoverable where a lost save is not.
    private static void PruneOldest(string dir, string justWritten, int retain)
    {
        // `retain` counts the file just written, so only retain-1 of the others are kept.
        var stale = Directory.GetFiles(dir, "*.json")
            .Where(file => !string.Equals(file, justWritten, StringComparison.Ordinal))
            .OrderDescending()
            .Skip(retain - 1)
            .ToArray();

        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
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
#pragma warning disable CA1870 // a cached SearchValues buys nothing on a once-per-load/save path
        if (string.IsNullOrWhiteSpace(checkpointId) || checkpointId.IndexOfAny(UnsafeIdChars) >= 0)
#pragma warning restore CA1870
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
