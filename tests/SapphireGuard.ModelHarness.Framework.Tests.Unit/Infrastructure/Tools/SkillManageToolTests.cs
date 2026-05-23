using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tools;

public sealed class SkillManageToolTests
{
    private static ToolCall Call(object args) =>
        new("c1", "skill_manage", JsonSerializer.SerializeToElement(args));

    private static ToolContext Ctx() => ToolContext.Empty("task", "c1");

    [Fact]
    public async Task Save_PersistsSkill()
    {
        var store = new InMemorySkillStore();
        var tool = new SkillManageTool(store);

        var result = await tool.ExecuteAsync(
            Call(new { action = "save", name = "deploy", description = "d", when_to_use = "u", body = "steps" }),
            Ctx(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(store.Skills.ContainsKey("deploy"));
        Assert.Equal("steps", store.Skills["deploy"].Body);
    }

    [Fact]
    public async Task Save_MissingRequiredField_ReturnsError()
    {
        var store = new InMemorySkillStore();
        var tool = new SkillManageTool(store);

        var result = await tool.ExecuteAsync(
            Call(new { action = "save", name = "deploy", description = "d", when_to_use = "u" }),
            Ctx(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(store.Skills);
    }

    [Fact]
    public async Task Delete_RemovesSkill()
    {
        var store = new InMemorySkillStore();
        await store.SaveAsync(new Framework.Skills.Skill("deploy", "d", "u", "b"), CancellationToken.None);
        var tool = new SkillManageTool(store);

        var result = await tool.ExecuteAsync(
            Call(new { action = "delete", name = "deploy" }), Ctx(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(store.Skills);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var tool = new SkillManageTool(new InMemorySkillStore());

        var result = await tool.ExecuteAsync(
            Call(new { action = "frobnicate", name = "deploy" }), Ctx(), CancellationToken.None);

        Assert.True(result.IsError);
    }
}
