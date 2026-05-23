using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

public sealed class InMemorySkillStore : ISkillStore
{
    private readonly Dictionary<string, Skill> _skills = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Skill> Skills => _skills;

    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillSummary>>([.. _skills.Values.Select(s => s.ToSummary())]);

    public Task<Skill?> GetAsync(string name, CancellationToken ct) =>
        Task.FromResult(_skills.GetValueOrDefault(name));

    public Task SaveAsync(Skill skill, CancellationToken ct)
    {
        _skills[skill.Name] = skill;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        _skills.Remove(name);
        return Task.CompletedTask;
    }
}
