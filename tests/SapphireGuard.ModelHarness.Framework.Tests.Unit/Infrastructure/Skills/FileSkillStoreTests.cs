using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

public sealed class FileSkillStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skilltest_" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsAllFields()
    {
        var store = new FileSkillStore(_dir);
        var skill = new Skill("deploy-modal", "Deploys to Modal", "when shipping to Modal",
            "1. Build\n2. Push\n3. Verify", "2.0.0");

        await store.SaveAsync(skill, CancellationToken.None);
        var loaded = await store.GetAsync("deploy-modal", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(skill.Name, loaded!.Name);
        Assert.Equal(skill.Description, loaded.Description);
        Assert.Equal(skill.WhenToUse, loaded.WhenToUse);
        Assert.Equal(skill.Body, loaded.Body);
        Assert.Equal("2.0.0", loaded.Version);
    }

    [Fact]
    public async Task List_ReturnsSummariesForSavedSkills()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("a", "desc a", "use a", "body a"), CancellationToken.None);
        await store.SaveAsync(new Skill("b", "desc b", "use b", "body b"), CancellationToken.None);

        var summaries = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Contains(summaries, s => s.Name == "a" && s.WhenToUse == "use a");
        Assert.Contains(summaries, s => s.Name == "b");
    }

    [Fact]
    public async Task Delete_RemovesSkill()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("gone", "d", "u", "b"), CancellationToken.None);

        await store.DeleteAsync("gone", CancellationToken.None);

        Assert.Null(await store.GetAsync("gone", CancellationToken.None));
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        var store = new FileSkillStore(_dir);
        Assert.Null(await store.GetAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task List_MissingDirectory_ReturnsEmpty()
    {
        var store = new FileSkillStore(_dir);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }
}
