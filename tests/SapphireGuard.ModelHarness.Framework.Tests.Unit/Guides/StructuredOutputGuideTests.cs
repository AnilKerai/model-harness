using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Output;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class StructuredOutputGuideTests
{
    private sealed record Triage(string Category, int Priority);

    private static readonly StructuredOutputContract<Triage> Contract = new();
    private static readonly StructuredOutputGuide<Triage> Sut = new(Contract);

    private static AgentState State() => AgentState.NewTask("triage this", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Contribute_StatesTheContractAsASystemSection()
    {
        var draft = new ContextDraft();

        await Sut.ContributeAsync(draft, State(), CancellationToken.None);

        // System sections are rebuilt every turn and never trimmed by HeadEvictionTrajectoryGuide, so
        // the contract cannot be compacted out from under the sensor that enforces it.
        Assert.Contains(Contract.SystemSection, draft.SystemSections);
    }

    [Fact]
    public async Task Contribute_DoesNotClobberTheSystemPrompt()
    {
        var draft = new ContextDraft { SystemPrompt = "You are a support triage agent." };

        await Sut.ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Equal("You are a support triage agent.", draft.SystemPrompt);
    }

    [Fact]
    public async Task Contribute_LeavesTheTrajectoryAlone()
    {
        var draft = new ContextDraft();

        await Sut.ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Empty(draft.TrajectoryMessages);
    }
}
