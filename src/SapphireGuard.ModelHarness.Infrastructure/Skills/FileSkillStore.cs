using System.Text;
using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Persists skills as <c>SKILL.md</c> files (YAML-style frontmatter + markdown body)
/// under a directory, one file per skill keyed by a sanitised name. Mirrors
/// <c>FileCheckpointStore</c>. Frontmatter is parsed by a minimal hand-rolled reader
/// to avoid a YAML dependency.
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
            list.Add(ParseSkill(Path.GetFileNameWithoutExtension(file), text).ToSummary());
        }
        return list;
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_dir, FileName(name));
        if (!File.Exists(path))
            return null;

        var text = await File.ReadAllTextAsync(path, ct);
        return ParseSkill(name, text);
    }

    public async Task SaveAsync(Skill skill, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, FileName(skill.Name)), Serialize(skill), ct);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        var path = Path.Combine(_dir, FileName(name));
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static string Serialize(Skill s)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(OneLine(s.Name)).Append('\n');
        sb.Append("description: ").Append(OneLine(s.Description)).Append('\n');
        sb.Append("when_to_use: ").Append(OneLine(s.WhenToUse)).Append('\n');
        sb.Append("version: ").Append(OneLine(s.Version)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(s.Body.Trim()).Append('\n');
        return sb.ToString();
    }

    private static Skill ParseSkill(string fallbackName, string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        var body = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in normalized[4..end].Split('\n'))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                        meta[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
                body = normalized[(end + 4)..].TrimStart('\n');
            }
        }

        return new Skill(
            meta.GetValueOrDefault("name", fallbackName),
            meta.GetValueOrDefault("description", ""),
            meta.GetValueOrDefault("when_to_use", ""),
            body.Trim(),
            meta.GetValueOrDefault("version", "1.0.0"));
    }

    private static string OneLine(string v) => v.Replace("\r", " ").Replace("\n", " ").Trim();

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
