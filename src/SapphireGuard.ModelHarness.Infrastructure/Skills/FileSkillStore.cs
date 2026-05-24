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
        await File.WriteAllTextAsync(
            Path.Combine(_dir, FileName(skill.Name)),
            SkillDocumentParser.Serialize(skill),
            ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_dir, FileName(name));
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static string FileName(string name)
    {
        var safe = name;
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        return safe + ".md";
    }

    private static string Expand(string dir) =>
        dir.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                dir[1..].TrimStart('/', '\\'))
            : dir;
}
