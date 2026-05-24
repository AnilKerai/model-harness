using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Aggregates an agent store (writable) with one or more user-defined stores (read-only
/// by routing). Reads merge across all stores; the agent store wins on name collision so
/// the agent can shadow and refine a user-defined skill. Writes (save / delete) are
/// routed exclusively to the agent store — user-defined skills are never mutated.
/// </summary>
public sealed class CompositeSkillStore(
    ISkillStore agentStore,
    IReadOnlyList<ISkillStore> userStores) : ISkillStore
{
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct)
    {
        var merged = new Dictionary<string, SkillSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in userStores)
            foreach (var summary in await store.ListAsync(ct))
                merged[summary.Name] = summary;

        foreach (var summary in await agentStore.ListAsync(ct))
            merged[summary.Name] = summary;

        return [.. merged.Values];
    }

    public async Task<Skill?> GetAsync(string name, CancellationToken ct)
    {
        var skill = await agentStore.GetAsync(name, ct);
        if (skill is not null) return skill;

        foreach (var store in userStores)
        {
            skill = await store.GetAsync(name, ct);
            if (skill is not null) return skill;
        }

        return null;
    }

    public Task SaveAsync(Skill skill, CancellationToken ct) =>
        agentStore.SaveAsync(skill, ct);

    public Task DeleteAsync(string name, CancellationToken ct) =>
        agentStore.DeleteAsync(name, ct);
}
