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

    public ISkillStore Build() =>
        AgentDirectory is null
            ? throw new InvalidOperationException(
                "Call WithAgentSkillStore before WithUserSkillStore.")
            : UserDirectories.Count == 0
                ? new FileSkillStore(AgentDirectory)
                : new CompositeSkillStore(
                    new FileSkillStore(AgentDirectory),
                    UserDirectories.Select(d => (ISkillStore)new FileSkillStore(d)).ToList());
}
