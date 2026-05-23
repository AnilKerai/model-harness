namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// A reusable procedure the agent has captured from past work. Procedural memory:
/// stored as data and surfaced into the prompt, never executed as code.
/// </summary>
public sealed record Skill(
    string Name,
    string Description,
    string WhenToUse,
    string Body,
    string Version = "1.0.0")
{
    public SkillSummary ToSummary() => new(Name, Description, WhenToUse);
}
