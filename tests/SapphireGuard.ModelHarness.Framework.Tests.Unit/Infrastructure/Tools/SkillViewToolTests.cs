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
    public async Task ExistingSkill_ReturnsBody()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Skill("deploy", "Deploys things", "when deploying", "step one then step two"),
            CancellationToken.None);
        var tool = new SkillViewTool(store);

        var result = await tool.ExecuteAsync(Call("deploy"), Ctx(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("step one then step two", result.Content);
        Assert.Contains("when deploying", result.Content);
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
