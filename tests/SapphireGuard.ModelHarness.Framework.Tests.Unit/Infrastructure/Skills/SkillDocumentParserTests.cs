using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Skills;

public sealed class SkillDocumentParserTests
{
    private static string Doc(
        string? name = "deploy-modal",
        string? description = "Deploys to Modal",
        string? whenToUse = "when shipping to Modal",
        string? version = "2.0.0",
        string body = "1. Build\n2. Push\n3. Verify") =>
        $"""
        ---
        name: {name}
        description: {description}
        when_to_use: {whenToUse}
        version: {version}
        ---

        {body}
        """;

    // ── Parse: required fields ────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidDocument_AllFieldsPopulated()
    {
        var skill = SkillDocumentParser.Parse(Doc());

        Assert.Equal("deploy-modal", skill.Name);
        Assert.Equal("Deploys to Modal", skill.Description);
        Assert.Equal("when shipping to Modal", skill.WhenToUse);
        Assert.Equal("2.0.0", skill.Version);
        Assert.Equal("1. Build\n2. Push\n3. Verify", skill.Body);
    }

    [Fact]
    public void Parse_MissingName_Throws()
    {
        var doc = "---\ndescription: A description\n---\n\nBody.";

        var ex = Assert.Throws<InvalidOperationException>(() => SkillDocumentParser.Parse(doc));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyName_Throws()
    {
        var doc = "---\nname:   \ndescription: A description\n---\n\nBody.";

        Assert.Throws<InvalidOperationException>(() => SkillDocumentParser.Parse(doc));
    }

    [Fact]
    public void Parse_MissingDescription_Throws()
    {
        var doc = "---\nname: my-skill\n---\n\nBody.";

        var ex = Assert.Throws<InvalidOperationException>(() => SkillDocumentParser.Parse(doc));
        Assert.Contains("description", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EmptyDescription_Throws()
    {
        var doc = "---\nname: my-skill\ndescription:   \n---\n\nBody.";

        Assert.Throws<InvalidOperationException>(() => SkillDocumentParser.Parse(doc));
    }

    [Fact]
    public void Parse_NoFrontmatter_Throws()
    {
        var doc = "Just a body with no frontmatter at all.";

        Assert.Throws<InvalidOperationException>(() => SkillDocumentParser.Parse(doc));
    }

    // ── Parse: optional fields default correctly ─────────────────────────────

    [Fact]
    public void Parse_MissingWhenToUse_DefaultsToEmpty()
    {
        var doc = "---\nname: my-skill\ndescription: A description\n---\n\nBody.";

        var skill = SkillDocumentParser.Parse(doc);

        Assert.Equal("", skill.WhenToUse);
    }

    [Fact]
    public void Parse_MissingVersion_DefaultsTo100()
    {
        var doc = "---\nname: my-skill\ndescription: A description\n---\n\nBody.";

        var skill = SkillDocumentParser.Parse(doc);

        Assert.Equal("1.0.0", skill.Version);
    }

    // ── Parse: body handling ─────────────────────────────────────────────────

    [Fact]
    public void Parse_Body_IsTrimmed()
    {
        var doc = "---\nname: my-skill\ndescription: desc\n---\n\n\n  Step 1.\n\n";

        var skill = SkillDocumentParser.Parse(doc);

        Assert.Equal("Step 1.", skill.Body);
    }

    [Fact]
    public void Parse_CrlfLineEndings_NormalisedCorrectly()
    {
        var doc = "---\r\nname: my-skill\r\ndescription: desc\r\n---\r\n\r\nBody.";

        var skill = SkillDocumentParser.Parse(doc);

        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("Body.", skill.Body);
    }

    // ── Serialize / Parse roundtrip ───────────────────────────────────────────

    [Fact]
    public void Roundtrip_AllFields_Preserved()
    {
        var original = new Skill("deploy-modal", "Deploys to Modal", "when shipping to Modal",
            "1. Build\n2. Push\n3. Verify", "2.0.0");

        var serialized = SkillDocumentParser.Serialize(original);
        var parsed = SkillDocumentParser.Parse(serialized);

        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.Description, parsed.Description);
        Assert.Equal(original.WhenToUse, parsed.WhenToUse);
        Assert.Equal(original.Body, parsed.Body);
        Assert.Equal(original.Version, parsed.Version);
    }

    [Fact]
    public void Serialize_MultilineBodyFields_CollapsedToOneLine()
    {
        var skill = new Skill("s", "line1\nline2", "use\nit", "body", "1.0.0");

        var serialized = SkillDocumentParser.Serialize(skill);

        Assert.Contains("description: line1 line2", serialized);
        Assert.Contains("when_to_use: use it", serialized);
    }
}
