namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// A point-in-time snapshot of a skill captured before it was overwritten.
/// <see cref="Id"/> is the archive timestamp string (UTC, invariant culture, millisecond
/// resolution: yyyyMMddTHHmmssfffZ) and doubles as the lookup key for
/// <see cref="ISkillStore.GetVersionAsync"/>. History archived by earlier versions carries
/// second-resolution ids (yyyyMMddTHHmmssZ), which still list and load.
/// </summary>
public sealed record SkillVersion(string Id, DateTimeOffset ArchivedAt, Skill Skill);
