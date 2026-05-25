namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// A point-in-time snapshot of a skill captured before it was overwritten.
/// <see cref="Id"/> is the archive timestamp string (yyyyMMddTHHmmssZ) and doubles
/// as the lookup key for <see cref="ISkillStore.GetVersionAsync"/>.
/// </summary>
public sealed record SkillVersion(string Id, DateTimeOffset ArchivedAt, Skill Skill);
