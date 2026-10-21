using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

// ToolSelectorGuide is the one supporting guide that fails closed: its output is the dispatch allowlist,
// so "skip it and keep the seeded list" would offer and dispatch every registered tool.
public sealed class ToolSelectorGuideTests
{
    private static AgentState State() =>
        AgentState.NewTask("task", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 1m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static ContextDraft DraftWith(params string[] names) =>
        new() { AvailableTools = [.. names.Select(n => (ITool)new StubTool(n))] };

    [Fact]
    public async Task SelectorReturnsSubsetInItsOwnOrder_ReplacesAvailableTools()
    {
        var draft = DraftWith("a", "b", "c");

        await new ToolSelectorGuide(new FuncSelector(tools => [tools[2], tools[0]]))
            .ContributeAsync(draft, State(), CancellationToken.None);

        Assert.Equal(["c", "a"], draft.AvailableTools.Select(t => t.Name));
    }

    [Fact]
    public async Task SelectorThrows_EmptiesAvailableToolsAndRethrows()
    {
        var draft = DraftWith("a", "b");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ToolSelectorGuide(new FuncSelector(_ => throw new InvalidOperationException("policy lookup failed")))
                .ContributeAsync(draft, State(), CancellationToken.None));

        Assert.Equal("policy lookup failed", ex.Message);
        Assert.Empty(draft.AvailableTools);
    }

    [Fact]
    public async Task SelectorTimesOutOnItsOwn_HarnessTokenUntouched_EmptiesAvailableToolsAndRethrows()
    {
        // A remote permission service's HttpClient timeout is a TaskCanceledException with our token untouched:
        // a fault, not cancellation, so it must fail closed like any other.
        var draft = DraftWith("a", "b");

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            new ToolSelectorGuide(new FuncSelector(_ => throw new TaskCanceledException("permission service timed out")))
                .ContributeAsync(draft, State(), CancellationToken.None));

        Assert.Empty(draft.AvailableTools);
    }

    [Fact]
    public async Task SelectorReturnsNull_EmptiesAvailableToolsAndRethrows()
    {
        var draft = DraftWith("a", "b");

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new ToolSelectorGuide(new FuncSelector(_ => null!))
                .ContributeAsync(draft, State(), CancellationToken.None));

        Assert.Empty(draft.AvailableTools);
    }

    [Fact]
    public async Task HarnessCancelledWhileSelecting_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var draft = DraftWith("a");
        var guide = new ToolSelectorGuide(new FuncSelector(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guide.ContributeAsync(draft, State(), cts.Token));
    }

    private sealed class FuncSelector(Func<IReadOnlyList<ITool>, IReadOnlyList<ITool>> select) : IToolSelector
    {
        public Task<IReadOnlyList<ITool>> SelectAsync(IReadOnlyList<ITool> tools, AgentState state, CancellationToken ct) =>
            Task.FromResult(select(tools));
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolResult(call.CallId, "ok"));
    }
}
