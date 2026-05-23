using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class SkillsGuideTests
{
    private static AgentState State() =>
        AgentState.NewTask("task", new StateBudget
        {
            MaxTurns = 10, MaxContextTokens = 100_000, MaxCostUsd = 1m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });

    [Fact]
    public async Task RendersSkillCatalogueIntoSystemSection()
    {
        var store = new StubSkillStore([
            new SkillSummary("deploy-to-modal", "Deploys a service to Modal", "when shipping to Modal")
        ]);

        var draft = new ContextDraft();
        await new SkillsGuide(store).ContributeAsync(draft, State(), CancellationToken.None);

        var section = Assert.Single(draft.SystemSections);
        Assert.Contains("# Available skills", section);
        Assert.Contains("deploy-to-modal", section);
        Assert.Contains("when shipping to Modal", section);
    }

    [Fact]
    public async Task NoSkills_AddsNoSection()
    {
        var draft = new ContextDraft();

        await new SkillsGuide(new StubSkillStore([])).ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Empty(draft.SystemSections);
    }
}

file sealed class StubSkillStore(IReadOnlyList<SkillSummary> summaries) : ISkillStore
{
    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct) => Task.FromResult(summaries);
    public Task<Skill?> GetAsync(string name, CancellationToken ct) => Task.FromResult<Skill?>(null);
    public Task SaveAsync(Skill skill, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
}
