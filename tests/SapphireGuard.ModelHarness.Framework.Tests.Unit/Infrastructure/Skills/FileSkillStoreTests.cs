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

    // ── Hand-written files ───────────────────────────────────────────────────

    private static string SkillText(string name, string body = "Do it.", string description = "desc") =>
        $"---\nname: {name}\ndescription: {description}\nwhen_to_use: when\nversion: 1.0.0\n---\n\n{body}\n";

    private void WriteFile(string fileName, string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, fileName), text);
    }

    [Fact]
    public async Task List_MalformedFile_IsSkippedAndTheRestListed()
    {
        WriteFile("good.md", SkillText("good"));
        WriteFile("bad.md", "---\nname: bad\n---\n\nno description\n");

        var summaries = await new FileSkillStore(_dir).ListAsync(CancellationToken.None);

        Assert.Equal("good", Assert.Single(summaries).Name);
    }

    [Fact]
    public async Task List_NonSkillMarkdown_IsSkipped()
    {
        WriteFile("README.md", "# My skills\n\nSome notes, no frontmatter.\n");
        WriteFile("empty.md", "");
        WriteFile("good.md", SkillText("good"));

        var summaries = await new FileSkillStore(_dir).ListAsync(CancellationToken.None);

        Assert.Equal("good", Assert.Single(summaries).Name);
    }

    [Fact]
    public async Task List_FileNameDiffersFromFrontmatterName_ListedUnderFrontmatterName()
    {
        WriteFile("order-enquiry.md", SkillText("answer-order-enquiry"));

        var summaries = await new FileSkillStore(_dir).ListAsync(CancellationToken.None);

        Assert.Equal("answer-order-enquiry", Assert.Single(summaries).Name);
    }

    [Fact]
    public async Task Get_FileNameDiffersFromFrontmatterName_FoundByFrontmatterName()
    {
        WriteFile("order-enquiry.md", SkillText("answer-order-enquiry", body: "Look up the order."));
        var store = new FileSkillStore(_dir);

        var loaded = await store.GetAsync("answer-order-enquiry", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("answer-order-enquiry", loaded!.Name);
        Assert.Equal("Look up the order.", loaded.Body);
    }

    [Fact]
    public async Task Get_FrontmatterNameMatchIsCaseInsensitive()
    {
        WriteFile("order-enquiry.md", SkillText("Answer-Order-Enquiry"));

        var loaded = await new FileSkillStore(_dir).GetAsync("answer-order-enquiry", CancellationToken.None);

        Assert.Equal("Answer-Order-Enquiry", loaded!.Name);
    }

    [Fact]
    public async Task Get_ExactFileNameMatch_StillWorks()
    {
        WriteFile("deploy.md", SkillText("deploy", body: "Ship it."));

        var loaded = await new FileSkillStore(_dir).GetAsync("deploy", CancellationToken.None);

        Assert.Equal("Ship it.", loaded!.Body);
    }

    [Fact]
    public async Task Get_FileNameMatchesButFrontmatterNameDoesNot_IsNotReturnedUnderTheFileName()
    {
        WriteFile("order-enquiry.md", SkillText("answer-order-enquiry"));

        Assert.Null(await new FileSkillStore(_dir).GetAsync("order-enquiry", CancellationToken.None));
    }

    [Fact]
    public async Task Get_DirectFileMalformed_FallsThroughToAnotherFileWithThatName()
    {
        WriteFile("deploy.md", "not a skill at all");
        WriteFile("zz-deploy-notes.md", SkillText("deploy", body: "From the other file."));

        var loaded = await new FileSkillStore(_dir).GetAsync("deploy", CancellationToken.None);

        Assert.Equal("From the other file.", loaded!.Body);
    }

    [Fact]
    public async Task Get_OnlyMalformedFiles_ReturnsNullWithoutThrowing()
    {
        WriteFile("deploy.md", "not a skill at all");
        WriteFile("other.md", "---\ndescription: no name\n---\n");

        Assert.Null(await new FileSkillStore(_dir).GetAsync("deploy", CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateNames_FileNamedAfterTheSkillWins_InGetAndList()
    {
        // "a-copy.md" sorts before "deploy.md", but deploy.md is the skill's own file.
        WriteFile("a-copy.md", SkillText("deploy", body: "copy"));
        WriteFile("deploy.md", SkillText("deploy", body: "original", description: "original desc"));
        var store = new FileSkillStore(_dir);

        var loaded = await store.GetAsync("deploy", CancellationToken.None);
        var summaries = await store.ListAsync(CancellationToken.None);

        Assert.Equal("original", loaded!.Body);
        var summary = Assert.Single(summaries);
        Assert.Equal("original desc", summary.Description);
    }

    [Fact]
    public async Task DuplicateNames_NoneNamedAfterTheSkill_FirstByOrdinalFileNameWins()
    {
        WriteFile("b.md", SkillText("deploy", body: "from b", description: "b desc"));
        WriteFile("a.md", SkillText("deploy", body: "from a", description: "a desc"));
        WriteFile("c.md", SkillText("deploy", body: "from c", description: "c desc"));
        var store = new FileSkillStore(_dir);

        var loaded = await store.GetAsync("deploy", CancellationToken.None);
        var summaries = await store.ListAsync(CancellationToken.None);

        Assert.Equal("from a", loaded!.Body);
        Assert.Equal("a desc", Assert.Single(summaries).Description);
    }

    [Fact]
    public async Task List_ReturnsSkillsInOrdinalFileNameOrder()
    {
        WriteFile("c.md", SkillText("c"));
        WriteFile("a.md", SkillText("a"));
        WriteFile("b.md", SkillText("b"));

        var summaries = await new FileSkillStore(_dir).ListAsync(CancellationToken.None);

        Assert.Equal(["a", "b", "c"], summaries.Select(s => s.Name));
    }

    // ── Names the agent supplies ─────────────────────────────────────────────

    [Theory]
    [InlineData("deploy ")]
    [InlineData(" deploy")]
    [InlineData("deploy\n")]
    public async Task Save_NameWithStrayWhitespace_IsListedAndLoadableUnderTheCleanName(string messyName)
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill(messyName, "d", "u", "b"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dir, "deploy.md")));
        Assert.Equal("deploy", Assert.Single(await store.ListAsync(CancellationToken.None)).Name);
        Assert.NotNull(await store.GetAsync("deploy", CancellationToken.None));
        Assert.NotNull(await store.GetAsync(messyName, CancellationToken.None));
    }

    [Fact]
    public async Task Save_NameWithEmbeddedNewline_IsListedAndLoadableUnderTheFoldedName()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("deploy\nmodal", "d", "u", "b"), CancellationToken.None);

        var listed = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("deploy modal", listed.Name);
        Assert.NotNull(await store.GetAsync(listed.Name, CancellationToken.None));
    }

    [Fact]
    public async Task Save_TwiceWithDifferentWhitespace_OverwritesAndArchivesOneSkill()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("deploy", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("deploy ", "d", "u", "v2"), CancellationToken.None);

        Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("v2", (await store.GetAsync("deploy", CancellationToken.None))!.Body);
        Assert.Equal("v1", Assert.Single(await store.ListVersionsAsync("deploy ", CancellationToken.None)).Skill.Body);
    }

    [Fact]
    public async Task Delete_NameWithStrayWhitespace_RemovesTheSkill()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("deploy", "d", "u", "b"), CancellationToken.None);

        await store.DeleteAsync(" deploy\n", CancellationToken.None);

        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" .. ")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_ReservedName_IsRejectedAndWritesNothing(string name)
    {
        var store = new FileSkillStore(_dir);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(new Skill(name, "d", "u", "b"), CancellationToken.None));

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFileSystemEntries(_dir).Any());
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ReadAndDelete_ReservedName_AreHarmlessNoOps(string name)
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("real", "d", "u", "b"), CancellationToken.None);

        await store.DeleteAsync(name, CancellationToken.None);

        Assert.Null(await store.GetAsync(name, CancellationToken.None));
        Assert.Empty(await store.ListVersionsAsync(name, CancellationToken.None));
        Assert.Null(await store.GetVersionAsync(name, "20260101T000000Z", CancellationToken.None));
        Assert.Single(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Save_LeavesNoTempFileBehind()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);

        Assert.Equal(["s.md"], Directory.EnumerateFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_OverMalformedFile_ReplacesItWithAValidSkill()
    {
        WriteFile("s.md", "garbage");
        var store = new FileSkillStore(_dir);

        await store.SaveAsync(new Skill("s", "d", "u", "fixed"), CancellationToken.None);

        Assert.Equal("fixed", (await store.GetAsync("s", CancellationToken.None))!.Body);
    }

    // ── History ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListVersions_MalformedArchive_IsSkipped()
    {
        var store = new FileSkillStore(_dir);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, ".history", "s", "20200101T000000Z.md"), "torn");

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal("v1", Assert.Single(versions).Skill.Body);
    }

    [Fact]
    public async Task ListVersions_MalformedArchiveAmongLegacyAndMillisecondIds_IsSkippedAndTheRestOrderedByInstant()
    {
        // The two ListVersionsAsync behaviours together: a torn archive is skipped, and the
        // surviving legacy and millisecond ids are ordered by instant, not by filename.
        var clock = new TestClock(T0);
        var store = new FileSkillStore(_dir, clock);
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None); // archives v1 at T0
        var historyDir = Path.Combine(_dir, ".history", "s");
        File.Move(Path.Combine(historyDir, "20261020T120000000Z.md"), Path.Combine(historyDir, "20261020T120000Z.md"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await store.SaveAsync(new Skill("s", "d", "u", "v3"), CancellationToken.None); // archives v2 at T0+500ms
        File.WriteAllText(Path.Combine(historyDir, "20261020T120000250Z.md"), "torn");

        var versions = await store.ListVersionsAsync("s", CancellationToken.None);

        Assert.Equal(["20261020T120000500Z", "20261020T120000Z"], versions.Select(v => v.Id));
        Assert.Equal(["v2", "v1"], versions.Select(v => v.Skill.Body));
    }

    [Fact]
    public async Task Save_OverMalformedFile_ThenListVersions_DoesNotThrow()
    {
        WriteFile("s.md", "garbage");
        var store = new FileSkillStore(_dir);

        await store.SaveAsync(new Skill("s", "d", "u", "fixed"), CancellationToken.None);

        Assert.Empty(await store.ListVersionsAsync("s", CancellationToken.None));
    }

    [Fact]
    public async Task GetVersion_MalformedArchive_ReturnsNull()
    {
        var historyDir = Path.Combine(_dir, ".history", "s");
        Directory.CreateDirectory(historyDir);
        File.WriteAllText(Path.Combine(historyDir, "20200101T000000Z.md"), "torn");

        Assert.Null(await new FileSkillStore(_dir).GetVersionAsync("s", "20200101T000000Z", CancellationToken.None));
    }

    [Theory]
    [InlineData("../../../secret")]
    [InlineData("../../../secret.md")]
    [InlineData("..\\..\\..\\secret")]
    public async Task GetVersion_IdWithPathSegments_DoesNotReadOutsideTheStore(string versionId)
    {
        // Layout: {_dir}/secret.md is a valid skill outside the store at {_dir}/store.
        var store = await StoreWithSecretOutsideAsync();

        Assert.Null(await store.GetVersionAsync("s", versionId, CancellationToken.None));
    }

    [Fact]
    public async Task GetVersion_AbsolutePathId_DoesNotReadOutsideTheStore()
    {
        var store = await StoreWithSecretOutsideAsync();

        Assert.Null(await store.GetVersionAsync("s", Path.Combine(_dir, "secret"), CancellationToken.None));
    }

    private async Task<FileSkillStore> StoreWithSecretOutsideAsync()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "secret.md"), SkillText("secret"));
        var store = new FileSkillStore(Path.Combine(_dir, "store"));
        await store.SaveAsync(new Skill("s", "d", "u", "v1"), CancellationToken.None);
        await store.SaveAsync(new Skill("s", "d", "u", "v2"), CancellationToken.None);
        return store;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public void Advance(TimeSpan by) => now += by;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
