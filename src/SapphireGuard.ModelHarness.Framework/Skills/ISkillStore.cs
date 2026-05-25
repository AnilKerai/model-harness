namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>
/// Stores the agent's procedural memory (skills). The read side backs the skills guide
/// and the skill-view tool; the write side backs the skill-manage tool the model invokes
/// when it wants to save a procedure for reuse. The default is <c>NullSkillStore</c>
/// (no-op); replace with a file- or database-backed implementation to persist skills
/// across runs.
/// </summary>
public interface ISkillStore
{
    /// <summary>Returns summaries (name + when-to-use) for all stored skills, shown in the skill catalogue each turn.</summary>
    Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct);

    /// <summary>Returns the full skill body by name, or <see langword="null"/> if not found.</summary>
    Task<Skill?> GetAsync(string name, CancellationToken ct);

    /// <summary>Creates or overwrites a skill.</summary>
    Task SaveAsync(Skill skill, CancellationToken ct);

    /// <summary>Removes a skill by name. No-op if the skill does not exist.</summary>
    Task DeleteAsync(string name, CancellationToken ct);

    /// <summary>
    /// Returns point-in-time snapshots archived before each overwrite, newest first.
    /// Returns an empty list when no history exists or the implementation does not
    /// support versioning.
    /// </summary>
    Task<IReadOnlyList<SkillVersion>> ListVersionsAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillVersion>>([]);

    /// <summary>
    /// Returns a specific archived snapshot by its <see cref="SkillVersion.Id"/>, or
    /// <see langword="null"/> if not found. The implementation does not support versioning
    /// by default.
    /// </summary>
    Task<Skill?> GetVersionAsync(string name, string versionId, CancellationToken ct) =>
        Task.FromResult<Skill?>(null);
}
