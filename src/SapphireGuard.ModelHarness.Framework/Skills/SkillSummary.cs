namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// The lightweight catalogue row for a skill — what the model sees every turn so
/// it knows a skill exists, without paying for the full procedure. The body is
/// pulled on demand via the skill-view tool (progressive disclosure).
/// </summary>
public sealed record SkillSummary(string Name, string Description, string WhenToUse);
