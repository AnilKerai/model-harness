using System.Globalization;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

public sealed class FileSkillStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task Save_SecondSave_ArchivesPreviousVersion()
    {
        var store = new FileSkillStore(_dir);
        var v1 = new Skill("greet", "Greets user", "when greeting", "Say hi.", "1.0.0");
        var v2 = new Skill("greet", "Greets user warmly", "when greeting", "Say hello!", "2.0.0");

        await store.SaveAsync(v1, CancellationToken.None);
        await store.SaveAsync(v2, CancellationToken.None);

        var current = await store.GetAsync("greet", CancellationToken.None);
        Assert.Equal("Say hello!", current!.Body);

        var versions = await store.ListVersionsAsync("greet", CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal("Say hi.", versions[0].Skill.Body);
    }

    [Fact]
    public async Task ListVersions_NoHistory_ReturnsEmpty()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("fresh", "d", "u", "b"), CancellationToken.None);

        Assert.Empty(await store.ListVersionsAsync("fresh", CancellationToken.None));
    }

    [Fact]
    public async Task ListVersions_MultipleOverwrites_ReturnedNewestFirst()
    {
        var clock = new TestClock(T0);
        var store = new FileSkillStore(_dir, clock);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.SaveAsync(new Skill("s", "d", "u", "v3"), CancellationToken.None);

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.Equal("v2", versions[0].Skill.Body);
        Assert.Equal(T0.AddSeconds(2), versions[0].ArchivedAt); // archived when v3 overwrote it
        Assert.Equal("v1", versions[1].Skill.Body);
        Assert.Equal(T0.AddSeconds(1), versions[1].ArchivedAt); // archived when v2 overwrote it
    }

    [Fact]
    public async Task Save_OverwritesInTheSameInstant_EachKeepsItsOwnArchivedVersion()
    {
        // A model can issue two skill_manage saves for one skill in a single response, milliseconds
        // apart. A clock that never advances is the limit case: every archive wants the same id.
        var store = new FileSkillStore(_dir, new TestClock(T0));

        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v3"), CancellationToken.None);

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.NotEqual(versions[0].Id, versions[1].Id);
        Assert.Equal("v2", versions[0].Skill.Body); // newest first
        Assert.Equal("v1", versions[1].Skill.Body);
        Assert.Equal(T0.AddMilliseconds(1), versions[0].ArchivedAt);
        Assert.Equal(T0, versions[1].ArchivedAt);
    }

    [Fact]
    public async Task Save_ArchiveIdAlreadyTaken_IsNotOverwritten()
    {
        // The id is claimed with CreateNew, so an existing file — written a moment ago by another
        // agent sharing this store — is skipped over rather than replaced.
        var store = new FileSkillStore(_dir, new TestClock(T0));
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        var historyDir = Path.Combine(_dir, ".history", "s");
        Directory.CreateDirectory(historyDir);
        var taken = Path.Combine(historyDir, "20261020T120000000Z.md");
        await File.WriteAllTextAsync(taken, "squatter");

        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);

        Assert.Equal("squatter", await File.ReadAllTextAsync(taken));
        var archived = await store.GetVersionAsync("s", "20261020T120000001Z", CancellationToken.None);
        Assert.Equal("v1", archived!.Body);
    }

    [Theory]
    [InlineData("th-TH")] // Buddhist calendar: 2026 renders as 2569
    [InlineData("ar-SA")] // Umm al-Qura calendar: 2026 renders as 1447/1448
    public async Task Save_UnderANonGregorianCalendarCulture_WritesTheInvariantId(string cultureName)
    {
        // The id was rendered and parsed with the ambient culture: a Gregorian id failed to parse under
        // ar-SA (the version vanished from ListVersionsAsync) and read as year 1483 under th-TH.
        var culture = new CultureInfo(cultureName);
        Assert.IsNotType<GregorianCalendar>(culture.DateTimeFormat.Calendar); // only live with ICU present

        var store = new FileSkillStore(_dir, new TestClock(T0));
        var original = CultureInfo.CurrentCulture;
        IReadOnlyList<SkillVersion> versions;
        try
        {
            CultureInfo.CurrentCulture = culture;
            await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
            await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
            versions = await store.ListVersionsAsync("s", CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var version = Assert.Single(versions);
        Assert.Equal("20261020T120000000Z", version.Id);
        Assert.Equal(T0, version.ArchivedAt);
    }

    [Fact]
    public async Task ListVersions_ArchivedUnderDifferentCalendarCultures_StillNewestFirst()
    {
        // Write the OLDER archive under th-TH and the newer under en-US — what a container rebuilt
        // with LANG=th_TH.UTF-8 produces. A culture-rendered id (2569…) outsorted every Gregorian one.
        var thai = new CultureInfo("th-TH");
        Assert.IsType<ThaiBuddhistCalendar>(thai.DateTimeFormat.Calendar);

        var clock = new TestClock(T0);
        var store = new FileSkillStore(_dir, clock);
        var original = CultureInfo.CurrentCulture;
        try
        {
            await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
            CultureInfo.CurrentCulture = thai;
            await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None); // archives v1
            clock.Advance(TimeSpan.FromSeconds(1));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            await store.SaveAsync(new Skill("s", "d", "u", "v3"), CancellationToken.None); // archives v2
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal(["v2", "v1"], versions.Select(v => v.Skill.Body));
    }

    [Fact]
    public async Task ListVersions_LegacySecondResolutionId_StillListed()
    {
        // History written before ids gained milliseconds: yyyyMMddTHHmmssZ.
        var store = new FileSkillStore(_dir, new TestClock(T0));
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
        var historyDir = Path.Combine(_dir, ".history", "s");
        File.Move(Path.Combine(historyDir, "20261020T120000000Z.md"), Path.Combine(historyDir, "20261020T120000Z.md"));

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        var version = Assert.Single(versions);
        Assert.Equal("20261020T120000Z", version.Id);
        Assert.Equal(T0, version.ArchivedAt);
        Assert.Equal("v1", version.Skill.Body);
        Assert.Equal("v1", (await store.GetVersionAsync("s", version.Id, CancellationToken.None))!.Body);
    }

    [Fact]
    public async Task ListVersions_LegacyAndMillisecondIds_OrderedByInstantNotFilename()
    {
        // A legacy id is a prefix of the same-second millisecond id minus "fff", so by filename
        // "…000500Z" sorts BEFORE "…000Z" even though it is half a second later.
        var clock = new TestClock(T0);
        var store = new FileSkillStore(_dir, clock);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None); // archives v1 at T0
        var historyDir = Path.Combine(_dir, ".history", "s");
        File.Move(Path.Combine(historyDir, "20261020T120000000Z.md"), Path.Combine(historyDir, "20261020T120000Z.md"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await store.SaveAsync(new Skill("s", "d", "u", "v3"), CancellationToken.None); // archives v2 at T0+500ms

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal(["20261020T120000500Z", "20261020T120000Z"], versions.Select(v => v.Id));
        Assert.Equal(["v2", "v1"], versions.Select(v => v.Skill.Body));
    }

    [Fact]
    public async Task GetVersion_ValidId_ReturnsSnapshot()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("s", "d", "u", "original"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "updated"), CancellationToken.None);

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);
        var loaded = await store.GetVersionAsync("s", versions[0].Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("original", loaded!.Body);
    }

    [Fact]
    public async Task GetVersion_UnknownId_ReturnsNull()
    {
        var store = new FileSkillStore(_dir);
        Assert.Null(await store.GetVersionAsync("s", "doesnotexist", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_DoesNotRemoveHistory()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);

        await store.DeleteAsync("s", CancellationToken.None);

        Assert.Null(await store.GetAsync("s", CancellationToken.None));
        Assert.Single(await store.ListVersionsAsync("s", CancellationToken.None));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
