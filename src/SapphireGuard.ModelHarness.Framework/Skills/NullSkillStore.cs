using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Skills;

/// <summary>No-op default. The skills guide emits nothing and the loop is unaffected.</summary>
[ExcludeFromCodeCoverage]
public sealed class NullSkillStore : ISkillStore
{
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillSummary>>([]);

    public Task<Skill?> GetAsync(string name, CancellationToken ct) =>
        Task.FromResult<Skill?>(null);

    public Task SaveAsync(Skill skill, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
