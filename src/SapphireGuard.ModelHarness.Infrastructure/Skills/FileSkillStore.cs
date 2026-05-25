using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Persists skills as <c>SKILL.md</c> files under a directory, one file per skill
/// keyed by a sanitised name. Parsing and serialisation are delegated to
/// <see cref="SkillDocumentParser"/>; this class is responsible only for file I/O.
/// </summary>
public sealed class FileSkillStore : ISkillStore
{
    private readonly string _dir;

    public FileSkillStore(string directory) => _dir = Expand(directory);

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
        foreach (var file in Directory.EnumerateFiles(historyDir, "*.md").Order().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (TryParseTimestamp(id, out var archivedAt))
                versions.Add(new SkillVersion(id, archivedAt, SkillDocumentParser.Parse(File.ReadAllText(file))));
        }
        return Task.FromResult<IReadOnlyList<SkillVersion>>(versions);
    }

    public async Task<Skill?> GetVersionAsync(string name, string versionId, CancellationToken ct)
    {
        var path = Path.Combine(HistoryDir(name), versionId + ".md");
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return SkillDocumentParser.Parse(text);
    }

    private async Task ArchiveAsync(string name, string currentPath, CancellationToken ct)
    {
        var historyDir = HistoryDir(name);
        Directory.CreateDirectory(historyDir);
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        await File.WriteAllTextAsync(Path.Combine(historyDir, id + ".md"),
            await File.ReadAllTextAsync(currentPath, ct), ct);
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
