namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// Stores the agent's procedural memory. The read side (<see cref="ListAsync"/> /
/// <see cref="GetAsync"/>) backs the skills guide and the skill-view tool; the write
/// side (<see cref="SaveAsync"/> / <see cref="DeleteAsync"/>) backs the skill-manage
/// tool the model invokes. Default is <see cref="NullSkillStore"/> (no-op); replace
/// with a file-, database-, or vector-backed implementation.
/// </summary>
public interface ISkillStore
{
    Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct);

    Task<Skill?> GetAsync(string name, CancellationToken ct);

    Task SaveAsync(Skill skill, CancellationToken ct);

    Task DeleteAsync(string name, CancellationToken ct);
}
