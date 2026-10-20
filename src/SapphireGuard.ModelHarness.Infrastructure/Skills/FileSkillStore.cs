using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Persists skills as <c>SKILL.md</c> files under a directory, one file per skill
/// keyed by a sanitised name. Parsing and serialisation are delegated to
/// <see cref="SkillDocumentParser"/>; this class is responsible only for file I/O.
/// </summary>
/// <remarks>
/// <para>
/// The directory may hold user-authored files, so a bad file affects only itself: any file that
/// cannot be read or parsed (no <c>name</c>/<c>description</c>, a stray <c>README.md</c>, a torn
/// write) is skipped <b>silently</b> by <see cref="ListAsync"/>, <see cref="GetAsync"/> and
/// <see cref="ListVersionsAsync"/>. <see cref="ISkillStore"/> has no diagnostics channel, so the
/// skip leaves no trace.
/// </para>
/// <para>
/// Skills are identified by their frontmatter <c>name</c>, not their filename. <see cref="GetAsync"/>
/// tries <c>{name}.md</c> first and falls back to scanning for a file whose frontmatter name matches,
/// so every skill <see cref="ListAsync"/> reports can be loaded under the name it is listed as. If
/// several files declare the same name (case-insensitive), the one whose filename is the name's own
/// <c>{name}.md</c> wins, otherwise the first by ordinal filename order, in both methods.
/// <see cref="SaveAsync"/> and <see cref="DeleteAsync"/> are keyed by filename (the agent writes
/// those files), using the same canonical name as the serialised frontmatter (single line, trimmed).
/// </para>
/// </remarks>
public sealed class FileSkillStore : ISkillStore
{
    private readonly string _dir;
    private readonly TimeProvider _time;

    public FileSkillStore(string directory, TimeProvider? timeProvider = null)
    {
        _dir = Expand(directory);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct)
    {
        var index = await IndexAsync(ct);
        return [.. index.Values.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => e.Skill.ToSummary())];
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct)
    {
        var wanted = SkillDocumentParser.OneLine(name);

        // Fast path: the file this store would have written. It only counts if the skill inside
        // really has this name; a hand-edited or malformed file falls through to the scan.
        if (StemFor(name) is { } stem)
        {
            var path = Path.Combine(_dir, stem + ".md");
            if (File.Exists(path)
                && await TryReadAsync(path, ct) is { } direct
                && string.Equals(direct.Name, wanted, StringComparison.OrdinalIgnoreCase))
                return direct;
        }

        var index = await IndexAsync(ct);
        return index.TryGetValue(wanted, out var entry) ? entry.Skill : null;
    }

    public async Task SaveAsync(Skill skill, CancellationToken ct)
    {
        var stem = StemFor(skill.Name)
            ?? throw new ArgumentException(
                $"Skill name '{skill.Name}' cannot be stored: it must be non-empty and not '.' or '..'.",
                nameof(skill));

        Directory.CreateDirectory(_dir);
        var current = Path.Combine(_dir, stem + ".md");
        if (File.Exists(current))
            await ArchiveAsync(stem, current, ct);

        // Write to a temp file then atomically rename, so a crash mid-write can't leave a torn
        // .md behind. The .tmp suffix keeps it out of the *.md glob.
        var tmp = Path.Combine(_dir, stem + ".tmp");
        await File.WriteAllTextAsync(tmp, SkillDocumentParser.Serialize(skill), ct);
        File.Move(tmp, current, overwrite: true);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        if (StemFor(name) is { } stem)
        {
            var path = Path.Combine(_dir, stem + ".md");
            if (File.Exists(path))
                File.Delete(path);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SkillVersion>> ListVersionsAsync(string name, CancellationToken ct)
    {
        if (StemFor(name) is not { } stem || !Directory.Exists(HistoryDir(stem)))
            return [];

        var versions = new List<SkillVersion>();
        foreach (var file in Directory.EnumerateFiles(HistoryDir(stem), "*.md").Order().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (TryParseTimestamp(id, out var archivedAt) && await TryReadAsync(file, ct) is { } skill)
                versions.Add(new SkillVersion(id, archivedAt, skill));
        }
        return versions;
    }

    public async Task<Skill?> GetVersionAsync(string name, string versionId, CancellationToken ct)
    {
        // The id is joined into a path, so a separator would read any .md outside the store
        // ("../../x"). Archive ids are timestamps, so one carrying path characters cannot exist.
        if (StemFor(name) is not { } stem
            || versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var path = Path.Combine(HistoryDir(stem), versionId + ".md");
        return File.Exists(path) ? await TryReadAsync(path, ct) : null;
    }

    // Every readable skill file, keyed by skill name. For a duplicated name the file that is the
    // name's own {name}.md wins, otherwise the first by ordinal filename: a deterministic choice
    // that ListAsync and GetAsync share, so the summary listed is the skill that gets loaded.
    private async Task<Dictionary<string, (string Path, Skill Skill)>> IndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<string, (string Path, Skill Skill)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_dir))
            return index;

        foreach (var file in Directory.EnumerateFiles(_dir, "*.md").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (await TryReadAsync(file, ct) is not { } skill)
                continue;

            if (!index.TryGetValue(skill.Name, out var kept) || (!IsHome(kept) && IsHome((file, skill))))
                index[skill.Name] = (file, skill);
        }
        return index;
    }

    // Whether the file is the one SaveAsync would write for the skill it holds.
    private static bool IsHome((string Path, Skill Skill) entry) =>
        string.Equals(Path.GetFileNameWithoutExtension(entry.Path), StemFor(entry.Skill.Name), StringComparison.Ordinal);

    // Null when the file is unreadable or not a valid skill: one bad file must not affect the rest.
    private static async Task<Skill?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return SkillDocumentParser.Parse(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task ArchiveAsync(string stem, string currentPath, CancellationToken ct)
    {
        var historyDir = HistoryDir(stem);
        Directory.CreateDirectory(historyDir);
        var id = _time.GetUtcNow().ToString("yyyyMMddTHHmmssZ");
        await File.WriteAllTextAsync(Path.Combine(historyDir, id + ".md"),
            await File.ReadAllTextAsync(currentPath, ct), ct);
    }

    private string HistoryDir(string stem) =>
        Path.Combine(_dir, ".history", stem);

    // The filename (sans extension) a skill name is stored under, or null when no file can hold it.
    // The name is canonicalised exactly as SkillDocumentParser.Serialize writes it into the
    // frontmatter (single line, trimmed), so the filename and the listed name cannot drift apart:
    // "deploy " must not write a "deploy .md" that lists as "deploy". "." and ".." are refused,
    // since ".." would resolve the history directory to the skills directory itself.
    private static string? StemFor(string name)
    {
        var stem = SkillDocumentParser.OneLine(name);
        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '_');
        return stem is "" or "." or ".." ? null : stem;
    }

    private static bool TryParseTimestamp(string id, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(id, "yyyyMMddTHHmmssZ",
            null, System.Globalization.DateTimeStyles.AssumeUniversal, out result);

    private static string Expand(string dir) =>
        dir.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                dir[1..].TrimStart('/', '\\'))
            : dir;
}
