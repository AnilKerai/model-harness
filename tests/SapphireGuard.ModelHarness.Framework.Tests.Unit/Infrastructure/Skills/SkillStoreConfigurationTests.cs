using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

/// <summary>
/// Verifies the four opt-in scenarios a harness author can configure via the fluent builder:
///   1. Learning only (WithLearning — writable, agent accumulates knowledge at runtime)
///   2. Skills only (WithSkills — pre-authored, read-only from the agent's perspective)
///   3. Both
///   4. Neither (no skill store methods called)
/// Skill tools are auto-registered alongside the store — no separate wiring needed.
/// </summary>
public sealed class SkillStoreConfigurationTests
{
    private static void AssertHasTool<T>(ModelHarnessBuilder builder) where T : ITool =>
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ITool) && d.ImplementationType == typeof(T));

    private static void AssertNoTool<T>(ModelHarnessBuilder builder) where T : ITool =>
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(ITool) && d.ImplementationType == typeof(T));

    // ── Scenario 1: agent skills only ────────────────────────────────────────

    [Fact]
    public void LearningOnly_RegistersStoreAndBothTools()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithLearning("~/skills/learned");

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ISkillStore));
        AssertHasTool<SkillViewTool>(builder);
        AssertHasTool<SkillManageTool>(builder);
    }

    // ── Scenario 2: user-defined skills only ─────────────────────────────────

    [Fact]
    public void SkillsOnly_RegistersStoreAndViewToolOnly()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithSkills("~/skills/builtin");

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ISkillStore));
        AssertHasTool<SkillViewTool>(builder);
        AssertNoTool<SkillManageTool>(builder);
    }

    [Fact]
    public async Task SkillsOnly_CompositeWithNullAgent_UserSkillReadableWriteIsNoOp()
    {
        var userStore = new InMemorySkillStoreWithSeed("guide");
        var store = new CompositeSkillStore(new NullSkillStore(), [userStore]);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("guide", list[0].Name);

        // Save is no-op — NullSkillStore agent discards it
        await store.SaveAsync(new Skill("new", "d", "w", "body"), CancellationToken.None);
        Assert.Null(await store.GetAsync("new", CancellationToken.None));
    }

    // ── Scenario 3: both ─────────────────────────────────────────────────────

    [Fact]
    public void Both_RegistersStoreAndBothTools()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithLearning("~/skills/learned")
               .WithSkills("~/skills/builtin");

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(ISkillStore));
        AssertHasTool<SkillViewTool>(builder);
        AssertHasTool<SkillManageTool>(builder);
    }

    [Fact]
    public void Both_MultipleUserStores_EachToolRegisteredExactlyOnce()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        builder.WithLearning("~/skills/learned")
               .WithSkills("~/skills/builtin")
               .WithSkills("~/skills/shared");

        Assert.Single(builder.Services, d => d.ServiceType == typeof(ISkillStore));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(ITool) && d.ImplementationType == typeof(SkillViewTool));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(ITool) && d.ImplementationType == typeof(SkillManageTool));
    }

    // ── Scenario 4: neither ──────────────────────────────────────────────────

    [Fact]
    public void Neither_NoStoreOrToolsRegistered()
    {
        var builder = new ModelHarnessBuilder(new ServiceCollection());

        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(ISkillStore));
        AssertNoTool<SkillViewTool>(builder);
        AssertNoTool<SkillManageTool>(builder);
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
