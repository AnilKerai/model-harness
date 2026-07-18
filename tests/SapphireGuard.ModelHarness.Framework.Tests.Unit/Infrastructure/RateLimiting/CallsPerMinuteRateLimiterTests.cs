using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.RateLimiting;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.RateLimiting;

public sealed class CallsPerMinuteRateLimiterTests
{
    private static readonly CallsPerMinuteRateLimiter Sut = new(callsPerMinute: 3);

    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 100, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep RecentStep() => ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
    private static ModelCallStep OldStep() => ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2));

    private static ModelCallStep ModelStep(DateTimeOffset timestamp) =>
        new(Guid.NewGuid(), timestamp,
            Prompt: [],
            Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m },
            Usage: Usage.Zero, Cost: 0m);

    private static AgentState WithSteps(params ModelCallStep[] steps)
    {
        var state = EmptyState();
        foreach (var step in steps)
            state = state.AppendStep(step);
        return state;
    }

    [Fact]
    public async Task Check_NoCallsInWindow_Passes()
    {
        var result = await Sut.CheckAsync(EmptyState(), CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_CallsBelowLimit_Passes()
    {
        var state = WithSteps(RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_CallsAtLimit_IsLimited()
    {
        var state = WithSteps(RecentStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_CallsAboveLimit_IsLimited()
    {
        var state = WithSteps(RecentStep(), RecentStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_OldCallsExceedLimit_Passes()
    {
        // Calls outside the 60s window should not count.
        var state = WithSteps(OldStep(), OldStep(), OldStep(), OldStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_MixedOldAndRecent_OnlyCountsRecent()
    {
        // 2 old + 2 recent = 2 in window, below limit of 3 → pass
        var state = WithSteps(OldStep(), OldStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_WhenLimited_RetryAfterIsPositive()
    {
        var state = WithSteps(RecentStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Check_WhenLimited_RetryAfterIsAtMostSixtySeconds()
    {
        var state = WithSteps(RecentStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.RetryAfter <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Check_WhenLimited_ReasonMentionsLimit()
    {
        var state = WithSteps(RecentStep(), RecentStep(), RecentStep());
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.Contains("3", result.Reason);
    }

    [Fact]
    public async Task Check_CustomLimit_RespectsIt()
    {
        var sut = new CallsPerMinuteRateLimiter(callsPerMinute: 1);
        var state = WithSteps(RecentStep());
        var result = await sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_CallExactlySixtySecondsOld_HasAgedOutOfTheWindow()
    {
        // RetryAfter targets exactly the instant a call turns 60s old, so the window's lower bound must
        // be exclusive: counting the call at that instant means a correctly-sized wait never clears the
        // limit, costing one wasted iteration (and a checkpoint write) every time.
        var now = T0.AddSeconds(60);
        var sut = new CallsPerMinuteRateLimiter(callsPerMinute: 1, new FixedClock(now));

        var result = await sut.CheckAsync(WithSteps(ModelStep(T0)), CancellationToken.None);

        Assert.False(result.IsLimited);
    }

    [Fact]
    public void Ctor_NonPositiveLimit_Throws()
    {
        // Without this the pass-guard is false for an empty window and the RetryAfter path indexes an
        // empty list — a misconfiguration surfacing as an IndexOutOfRangeException mid-run.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallsPerMinuteRateLimiter(callsPerMinute: 0));
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
