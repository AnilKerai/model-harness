using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class ToolCatalogueGuideTests
{
    private static AgentState State() =>
        AgentState.NewTask("task", new StateBudget
        {
            MaxTurns = 10, MaxContextTokens = 100_000, MaxCost = 1m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RendersAvailableToolsIntoSystemSection()
    {
        var draft = new ContextDraft { AvailableTools = [new StubTool("calc", "Does maths")] };

        await new ToolCatalogueGuide().ContributeAsync(draft, State(), CancellationToken.None);

        var section = Assert.Single(draft.SystemSections);
        Assert.Contains("# Available tools", section);
        Assert.Contains("calc", section);
        Assert.Contains("Does maths", section);
    }

    [Fact]
    public async Task NoTools_AddsNoSection()
    {
        var draft = new ContextDraft { AvailableTools = [] };

        await new ToolCatalogueGuide().ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Empty(draft.SystemSections);
    }
}

file sealed class StubTool(string name, string description) : ITool
{
    public string Name => name;
    public string Description => description;
    public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, "ok"));
}
