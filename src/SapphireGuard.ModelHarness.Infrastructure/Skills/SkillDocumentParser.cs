using System.Text;
using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Parses and serialises the SKILL.md format: YAML-style frontmatter followed by a
/// markdown body. Enforces the agentskills.io spec — <c>name</c> and <c>description</c>
/// are required; missing either throws <see cref="InvalidOperationException"/>.
/// <c>when_to_use</c> and <c>version</c> are framework extensions and are optional.
/// </summary>
internal static class SkillDocumentParser
{
    public static Skill Parse(string text)
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

        var name = meta.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "SKILL.md is missing a required 'name' field in its frontmatter.");

        var description = meta.GetValueOrDefault("description");
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException(
                $"Skill '{name}' is missing a required 'description' field in its frontmatter.");

        return new Skill(
            name,
            description,
            meta.GetValueOrDefault("when_to_use", ""),
            body.Trim(),
            meta.GetValueOrDefault("version", "1.0.0"));
    }

    public static string Serialize(Skill skill)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(OneLine(skill.Name)).Append('\n');
        sb.Append("description: ").Append(OneLine(skill.Description)).Append('\n');
        sb.Append("when_to_use: ").Append(OneLine(skill.WhenToUse)).Append('\n');
        sb.Append("version: ").Append(OneLine(skill.Version)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(skill.Body.Trim()).Append('\n');
        return sb.ToString();
    }

    private static string OneLine(string v) =>
        v.Replace("\r", " ").Replace("\n", " ").Trim();
}
