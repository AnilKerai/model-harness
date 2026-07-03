using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tools;

public sealed class SkillViewToolTests
{
    private static ToolCall Call(string name) =>
        new("c1", "skill_view", JsonSerializer.SerializeToElement(new { name }));

    private static ToolContext Ctx() => ToolContext.Empty("task", "c1");

    [Fact]
    public async Task ExistingSkill_PinsBody_AndAcksInContent()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("deploy", "Deploys things", "when deploying", "step one then step two"),
            CancellationToken.None);
        var tool = new SkillViewTool(store);

        var result = await tool.ExecuteAsync(Call("deploy"), Ctx(), CancellationToken.None);

        Assert.False(result.IsError);
        // The body is pinned into the persistent region, not returned as an evictable tool result.
        Assert.Contains("deploy", result.Content);               // a short ack naming the skill
        Assert.DoesNotContain("step one then step two", result.Content);
        var pin = Assert.Single(result.Pins!);
        Assert.Equal("Skill: deploy", pin.Label);
        Assert.Contains("step one then step two", pin.Content);
        Assert.Contains("when deploying", pin.Content);
    }

    [Fact]
    public async Task MissingSkill_ReturnsError()
    {
        var tool = new SkillViewTool(new InMemorySkillStore());

        var result = await tool.ExecuteAsync(Call("ghost"), Ctx(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
    }
}
