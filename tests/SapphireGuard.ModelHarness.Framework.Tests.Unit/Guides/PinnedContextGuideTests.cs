using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class PinnedContextGuideTests
{
    private static AgentState StateWith(params PinnedNote[] pins) =>
        AgentState.NewTask("t", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow) with { Pins = pins };

    [Fact]
    public async Task RendersEachPinAsASystemSection()
    {
        var state = StateWith(
            new PinnedNote("Skill: verify", "the verification procedure"),
            new PinnedNote("Contract", "exactly two tables"));
        var draft = new ContextDraft();

        await new PinnedContextGuide().ContributeAsync(draft, state, CancellationToken.None);

        Assert.Equal(2, draft.SystemSections.Count);
        Assert.Contains(draft.SystemSections, s => s.Contains("Skill: verify") && s.Contains("the verification procedure"));
        Assert.Contains(draft.SystemSections, s => s.Contains("Contract") && s.Contains("exactly two tables"));
    }

    [Fact]
    public async Task NoPins_AddsNothing()
    {
        var draft = new ContextDraft();

        await new PinnedContextGuide().ContributeAsync(draft, StateWith(), CancellationToken.None);

        Assert.Empty(draft.SystemSections);
    }
}
