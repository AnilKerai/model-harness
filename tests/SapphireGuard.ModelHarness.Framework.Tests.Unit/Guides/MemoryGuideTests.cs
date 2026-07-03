using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class MemoryGuideTests
{
    private sealed class RecordingMemoryStore : IMemoryStore
    {
        public string? LastQuery { get; private set; }

        public Task<IReadOnlyList<string>> RetrieveAsync(string query, int maxSnippets, CancellationToken ct)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<string>>([$"snippet for {query}"]);
        }
    }

    private static AgentState State() =>
        AgentState.NewTask("first message", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 1m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    [Fact]
    public async Task QueriesLatestUserMessage_NotTheFirst()
    {
        var store = new RecordingMemoryStore();
        var state = State().WithUserMessage("second message", DateTimeOffset.UtcNow).WithUserMessage("third message", DateTimeOffset.UtcNow);
        var draft = new ContextDraft();

        await new MemoryGuide(store).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Equal("third message", store.LastQuery);
        Assert.Single(draft.MemorySnippets);
    }

    [Fact]
    public async Task SingleTurnTask_QueriesTaskText()
    {
        var store = new RecordingMemoryStore();
        var draft = new ContextDraft();

        await new MemoryGuide(store).ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Equal("first message", store.LastQuery);
    }

    [Fact]
    public async Task NoUserMessageInTrajectory_FallsBackToTaskText()
    {
        var store = new RecordingMemoryStore();
        var state = State() with { Trajectory = [] };
        var draft = new ContextDraft();

        await new MemoryGuide(store).ContributeAsync(draft, state, CancellationToken.None);

        Assert.Equal("first message", store.LastQuery);
    }
}
