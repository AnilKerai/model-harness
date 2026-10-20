using System.Globalization;
using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Persists skills as <c>SKILL.md</c> files under a directory, one file per skill
/// keyed by a sanitised name. Parsing and serialisation are delegated to
/// <see cref="SkillDocumentParser"/>; this class is responsible only for file I/O.
/// Each overwrite archives the previous file as <c>.history/{name}/{id}.md</c>, where the id is a
/// UTC millisecond timestamp (<see cref="ArchiveIdFormat"/>, invariant culture) — see
/// <see cref="ArchiveAsync"/> for how same-millisecond saves stay distinct.
/// </summary>
public sealed class FileSkillStore : ISkillStore
{
    // Archive ids: millisecond resolution, so two overwrites a few milliseconds apart (a model can
    // issue two skill_manage saves in one response) don't share an id. The legacy format is
    // second-resolution and is parse-only, so history written before the change still lists.
    private const string ArchiveIdFormat = "yyyyMMddTHHmmssfffZ";
    private const string LegacyArchiveIdFormat = "yyyyMMddTHHmmssZ";

    private readonly string _dir;
    private readonly TimeProvider _time;

    public FileSkillStore(string directory, TimeProvider? timeProvider = null)
    {
        _dir = Expand(directory);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_dir))
            return [];

        var list = new List<SkillSummary>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.md"))
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, ct);
            list.Add(SkillDocumentParser.Parse(text).ToSummary());
        }
        return list;
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_dir, FileName(name));
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return SkillDocumentParser.Parse(text);
    }

    public async Task SaveAsync(Skill skill, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var current = Path.Combine(_dir, FileName(skill.Name));
        if (File.Exists(current))
            await ArchiveAsync(skill.Name, current, ct);

        await File.WriteAllTextAsync(current, SkillDocumentParser.Serialize(skill), ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_dir, FileName(name));
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkillVersion>> ListVersionsAsync(string name, CancellationToken ct)
    {
        var historyDir = HistoryDir(name);
        if (!Directory.Exists(historyDir))
            return Task.FromResult<IReadOnlyList<SkillVersion>>([]);

        var versions = new List<SkillVersion>();
        foreach (var file in Directory.EnumerateFiles(historyDir, "*.md"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (TryParseTimestamp(id, out var archivedAt))
                versions.Add(new SkillVersion(id, archivedAt, SkillDocumentParser.Parse(File.ReadAllText(file))));
        }

        // Newest first by the parsed instant, not the filename: legacy (second-resolution) and
        // current (millisecond) ids differ in length, so a filename sort misorders a mixed history.
        // The id breaks a tie so the order is deterministic.
        IReadOnlyList<SkillVersion> ordered = [.. versions
            .OrderByDescending(v => v.ArchivedAt)
            .ThenByDescending(v => v.Id, StringComparer.Ordinal)];
        return Task.FromResult(ordered);
    }

    public async Task<Skill?> GetVersionAsync(string name, string versionId, CancellationToken ct)
    {
        var path = Path.Combine(HistoryDir(name), versionId + ".md");
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return SkillDocumentParser.Parse(text);
    }

    /// <summary>
    /// Copies the current file into the skill's history under a timestamp id. The id is claimed
    /// atomically with <see cref="FileMode.CreateNew"/>: if the file already exists (two saves in
    /// the same millisecond, a clock that doesn't advance, or another agent sharing this store) the
    /// id moves forward one millisecond and tries again, so every overwrite keeps its own version
    /// and ids stay chronologically sortable.
    /// </summary>
    private async Task ArchiveAsync(string name, string currentPath, CancellationToken ct)
    {
        var historyDir = HistoryDir(name);
        Directory.CreateDirectory(historyDir);
        var content = await File.ReadAllBytesAsync(currentPath, ct);

        var archivedAt = _time.GetUtcNow();
        while (true)
        {
            var path = Path.Combine(historyDir, FormatArchiveId(archivedAt) + ".md");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                archivedAt = archivedAt.AddMilliseconds(1);
                continue;
            }

            await using (stream)
                await stream.WriteAsync(content, ct);
            return;
        }
    }

    private string HistoryDir(string name) =>
        Path.Combine(_dir, ".history", SafeName(name));

    private static string FileName(string name) => SafeName(name) + ".md";

    private static string SafeName(string name)
    {
        var safe = name;
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        return safe;
    }

    // Invariant culture, not the ambient one: under a non-Gregorian calendar locale (th-TH renders
    // 2026 as 2569, ar-SA as 1448) the id's digits differ, which broke sorting and, for ar-SA, parsing.
    // Same fix FileCheckpointStore made for its filename prefix.
    private static string FormatArchiveId(DateTimeOffset archivedAt) =>
        archivedAt.ToString(ArchiveIdFormat, CultureInfo.InvariantCulture);

    // Legacy ids written under a non-Gregorian-calendar culture hold that calendar's digits (e.g. a
    // th-TH id for 2026 reads 2569…). Nothing in the id says which calendar wrote it, so such ids
    // parse to the wrong instant — or, when the digits are not a valid Gregorian date, are skipped.
    private static bool TryParseTimestamp(string id, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(id, [ArchiveIdFormat, LegacyArchiveIdFormat],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result);

    private static string Expand(string dir) =>
        dir.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                dir[1..].TrimStart('/', '\\'))
            : dir;
}
