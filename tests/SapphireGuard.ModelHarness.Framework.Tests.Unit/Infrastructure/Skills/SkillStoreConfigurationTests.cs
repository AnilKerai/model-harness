using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

/// <summary>
/// Verifies the four opt-in scenarios a harness author can configure via the fluent builder:
///   1. Agent skills only (writable — learning)
///   2. User-defined skills only (read-only instructions)
///   3. Both
///   4. Neither (no skill store methods called)
/// </summary>
public sealed class SkillStoreConfigurationTests
{
    private static bool HasSkillStoreRegistration(ModelHarnessBuilder builder) =>
        builder.Services.Any(d => d.ServiceType == typeof(ISkillStore));

    // ── Scenario 1: agent skills only ────────────────────────────────────────

    [Fact]
    public void AgentOnly_RegistersISkillStore()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithAgentSkillStore("~/skills/learned");

        Assert.True(HasSkillStoreRegistration(builder));
    }

    // ── Scenario 2: user-defined skills only ─────────────────────────────────

    [Fact]
    public void UserOnly_RegistersISkillStore()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithUserSkillStore("~/skills/builtin");

        Assert.True(HasSkillStoreRegistration(builder));
    }

    [Fact]
    public async Task UserOnly_CompositeWithNullAgent_UserSkillReadableWriteIsNoOp()
    {
        // Directly exercise CompositeSkillStore with a NullSkillStore agent (the same
        // object graph WithUserSkillStore produces), without going through DI resolution.
        var userStore = new InMemorySkillStoreWithSeed("guide");
        var store = new CompositeSkillStore(new NullSkillStore(), [userStore]);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("guide", list[0].Name);

        // Save is no-op — NullSkillStore discards it
        await store.SaveAsync(new Skill("new", "d", "w", "body"), CancellationToken.None);
        Assert.Null(await store.GetAsync("new", CancellationToken.None));
    }

    // ── Scenario 3: both ─────────────────────────────────────────────────────

    [Fact]
    public void Both_RegistersISkillStore()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithAgentSkillStore("~/skills/learned")
               .WithUserSkillStore("~/skills/builtin");

        Assert.True(HasSkillStoreRegistration(builder));
    }

    [Fact]
    public void Both_RegistersExactlyOneISkillStore()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithAgentSkillStore("~/skills/learned")
               .WithUserSkillStore("~/skills/builtin")
               .WithUserSkillStore("~/skills/shared");

        Assert.Single(builder.Services, d => d.ServiceType == typeof(ISkillStore));
    }

    // ── Scenario 4: neither ──────────────────────────────────────────────────

    [Fact]
    public void Neither_NoISkillStoreRegistered()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        // No WithAgentSkillStore / WithUserSkillStore called
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(ISkillStore));
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private sealed class InMemorySkillStoreWithSeed(string skillName) : ISkillStore
    {
        private readonly Skill _skill = new(skillName, "desc", "when", "body");

        public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SkillSummary>>([_skill.ToSummary()]);

        public Task<Skill?> GetAsync(string name, CancellationToken ct) =>
            Task.FromResult<Skill?>(name == _skill.Name ? _skill : null);

        public Task SaveAsync(Skill skill, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}
