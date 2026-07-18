using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.RateLimiting;

public sealed class CompositeRateLimiterTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static IRateLimiter PassLimiter() =>
        new StubRateLimiter(RateLimitCheck.Pass);

    private static IRateLimiter LimitedLimiter(TimeSpan retryAfter, string reason = "limited") =>
        new StubRateLimiter(RateLimitCheck.Limited(retryAfter, reason));

    [Fact]
    public async Task Check_AllPass_Passes()
    {
        var sut = new CompositeRateLimiter([PassLimiter(), PassLimiter()]);
        var result = await sut.CheckAsync(EmptyState(), CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_OneLimited_IsLimited()
    {
        var sut = new CompositeRateLimiter([PassLimiter(), LimitedLimiter(TimeSpan.FromSeconds(10))]);
        var result = await sut.CheckAsync(EmptyState(), CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_MultipleLimited_ReturnsMostRestrictive()
    {
        var short_ = LimitedLimiter(TimeSpan.FromSeconds(5), "short");
        var long_ = LimitedLimiter(TimeSpan.FromSeconds(30), "long");
        var sut = new CompositeRateLimiter([short_, long_]);
        var result = await sut.CheckAsync(EmptyState(), CancellationToken.None);
        Assert.True(result.IsLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal("long", result.Reason);
    }

    [Fact]
    public async Task Check_EmptyComposite_Passes()
    {
        var sut = new CompositeRateLimiter([]);
        var result = await sut.CheckAsync(EmptyState(), CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_LimiterWithNoRetryAfter_DoesNotShadowOneThatReportsAWait()
    {
        // A null RetryAfter is "limited, duration unknown" — the weakest claim. The lifted `>` compare
        // is false whenever either side is null, so the first null won and the real 30s wait was
        // discarded; the loop then fell back to its own 10s default and under-waited.
        var unknown = new StubRateLimiter(new RateLimitCheck(IsLimited: true, RetryAfter: null, Reason: "unknown"));
        var known = LimitedLimiter(TimeSpan.FromSeconds(30), "known");

        var result = await new CompositeRateLimiter([unknown, known]).CheckAsync(EmptyState(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal("known", result.Reason);
    }

    [Fact]
    public async Task Check_EveryLimiterReportsNoRetryAfter_StillLimitedWithNoWait()
    {
        // Nothing to prefer, so the null survives and the loop applies its own default.
        var a = new StubRateLimiter(new RateLimitCheck(IsLimited: true, RetryAfter: null, Reason: "a"));
        var b = new StubRateLimiter(new RateLimitCheck(IsLimited: true, RetryAfter: null, Reason: "b"));

        var result = await new CompositeRateLimiter([a, b]).CheckAsync(EmptyState(), CancellationToken.None);

        Assert.True(result.IsLimited);
        Assert.Null(result.RetryAfter);
    }

    private sealed class StubRateLimiter(RateLimitCheck check) : IRateLimiter
    {
        public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct) =>
            Task.FromResult(check);
    }
}
