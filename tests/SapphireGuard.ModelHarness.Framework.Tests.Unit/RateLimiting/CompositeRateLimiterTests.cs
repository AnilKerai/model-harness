using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.RateLimiting;

public sealed class CompositeRateLimiterTests
{
    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxContextTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    });

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

    private sealed class StubRateLimiter(RateLimitCheck check) : IRateLimiter
    {
        public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct) =>
            Task.FromResult(check);
    }
}
