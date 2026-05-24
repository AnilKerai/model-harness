using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

public sealed class CompositeSkillStoreTests
{
    private static Skill MakeSkill(string name, string description = "desc") =>
        new(name, description, "when to use it", "## Steps\n1. Do the thing.");

    private static CompositeSkillStore Make(
        InMemorySkillStore agent,
        params InMemorySkillStore[] users) =>
        new(agent, users);

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_AgentSkillsOnly_ReturnsThem()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("alpha"), CancellationToken.None);

        var store = Make(agent);
        var list = await store.ListAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("alpha", list[0].Name);
    }

    [Fact]
    public async Task ListAsync_UserSkillsOnly_ReturnsThem()
    {
        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("beta"), CancellationToken.None);

        var store = Make(new InMemorySkillStore(), user);
        var list = await store.ListAsync(CancellationToken.None);

        Assert.Single(list);
        Assert.Equal("beta", list[0].Name);
    }

    [Fact]
    public async Task ListAsync_NoClash_MergesAll()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("alpha"), CancellationToken.None);

        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("beta"), CancellationToken.None);

        var store = Make(agent, user);
        var list = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Name == "alpha");
        Assert.Contains(list, s => s.Name == "beta");
    }

    [Fact]
    public async Task ListAsync_NameClash_AgentWins()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("shared", "agent version"), CancellationToken.None);

        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("shared", "user version"), CancellationToken.None);

        var store = Make(agent, user);
        var list = await store.ListAsync(CancellationToken.None);

        var hit = Assert.Single(list, s => s.Name == "shared");
        Assert.Equal("agent version", hit.Description);
    }

    // ── GetAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_InAgentStore_ReturnsIt()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("alpha"), CancellationToken.None);

        var store = Make(agent);
        var skill = await store.GetAsync("alpha", CancellationToken.None);

        Assert.NotNull(skill);
        Assert.Equal("alpha", skill!.Name);
    }

    [Fact]
    public async Task GetAsync_NotInAgentButInUser_ReturnsUserSkill()
    {
        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("beta"), CancellationToken.None);

        var store = Make(new InMemorySkillStore(), user);
        var skill = await store.GetAsync("beta", CancellationToken.None);

        Assert.NotNull(skill);
        Assert.Equal("beta", skill!.Name);
    }

    [Fact]
    public async Task GetAsync_NotFoundAnywhere_ReturnsNull()
    {
        var store = Make(new InMemorySkillStore(), new InMemorySkillStore());
        var skill = await store.GetAsync("missing", CancellationToken.None);

        Assert.Null(skill);
    }

    [Fact]
    public async Task GetAsync_NameClash_AgentVersionReturned()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("shared", "agent version"), CancellationToken.None);

        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("shared", "user version"), CancellationToken.None);

        var store = Make(agent, user);
        var skill = await store.GetAsync("shared", CancellationToken.None);

        Assert.Equal("agent version", skill!.Description);
    }

    // ── SaveAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WritesToAgentStoreOnly()
    {
        var agent = new InMemorySkillStore();
        var user = new InMemorySkillStore();
        var store = Make(agent, user);

        await store.SaveAsync(MakeSkill("new-skill"), CancellationToken.None);

        Assert.True(agent.Skills.ContainsKey("new-skill"));
        Assert.Empty(user.Skills);
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_RemovesFromAgentStoreOnly()
    {
        var agent = new InMemorySkillStore();
        await agent.SaveAsync(MakeSkill("alpha"), CancellationToken.None);

        var user = new InMemorySkillStore();
        await user.SaveAsync(MakeSkill("alpha"), CancellationToken.None);

        var store = Make(agent, user);
        await store.DeleteAsync("alpha", CancellationToken.None);

        Assert.False(agent.Skills.ContainsKey("alpha"));
        Assert.True(user.Skills.ContainsKey("alpha"));
    }
}
