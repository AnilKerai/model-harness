using SapphireGuard.ModelHarness.Framework.Skills;

namespace SapphireGuard.ModelHarness.Infrastructure.Skills;

/// <summary>
/// Mutable accumulator shared by <c>WithAgentSkillStore</c> and <c>WithUserSkillStore</c>
/// during builder configuration. Resolved once at container build time to produce the
/// correct <see cref="ISkillStore"/> implementation.
/// </summary>
internal sealed class SkillStoreConfiguration
{
    public string? AgentDirectory { get; set; }
    public List<string> UserDirectories { get; } = [];

    public ISkillStore Build()
    {
        ISkillStore agentStore = AgentDirectory is not null
            ? new FileSkillStore(AgentDirectory)
            : new NullSkillStore();

        return UserDirectories.Count == 0
            ? agentStore
            : new CompositeSkillStore(
                agentStore,
                UserDirectories.Select(d => (ISkillStore)new FileSkillStore(d)).ToList());
    }
}
