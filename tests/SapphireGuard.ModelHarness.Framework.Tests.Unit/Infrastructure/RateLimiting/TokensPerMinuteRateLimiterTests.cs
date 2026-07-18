using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.RateLimiting;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.RateLimiting;

public sealed class TokensPerMinuteRateLimiterTests
{
    private static readonly TokensPerMinuteRateLimiter Sut = new(tokensPerMinute: 1_000);

    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 100, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep RecentStep(int inputTokens, int outputTokens) =>
        ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10), inputTokens, outputTokens);

    private static ModelCallStep OldStep(int inputTokens, int outputTokens) =>
        ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2), inputTokens, outputTokens);

    private static ModelCallStep ModelStep(DateTimeOffset timestamp, int inputTokens, int outputTokens) =>
        new(Guid.NewGuid(), timestamp,
            Prompt: [],
            Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = new Usage(inputTokens, outputTokens), Cost = 0m },
            Usage: new Usage(inputTokens, outputTokens), Cost: 0m);

    // A call whose client declared its provider's rate-limit accounting: the prompt was billed in full, but only
    // some of it counts toward the input limit (Anthropic excludes cache reads).
    private static ModelCallStep CachedStep(int inputTokens, int outputTokens, int cacheReadTokens) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
            Prompt: [],
            Response: new ModelResponse
            {
                Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn,
                Usage = new Usage(inputTokens, outputTokens), Cost = 0m,
                CachedInputTokens = cacheReadTokens,
                InputTokensTowardRateLimit = inputTokens - cacheReadTokens
            },
            Usage: new Usage(inputTokens, outputTokens), Cost: 0m);

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
    public async Task Check_TokensBelowLimit_Passes()
    {
        var state = WithSteps(RecentStep(400, 100)); // 500 tokens, below 1000
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_TokensAtLimit_IsLimited()
    {
        var state = WithSteps(RecentStep(600, 400)); // 1000 tokens, at limit
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_TokensAboveLimit_IsLimited()
    {
        var state = WithSteps(RecentStep(800, 400)); // 1200 tokens, above limit
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_SumsInputAndOutputTokens()
    {
        // Two steps: 400+400 = 800, plus 100+200 = 300 → total 1100, above limit
        var state = WithSteps(RecentStep(400, 400), RecentStep(100, 200));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_OldCallsExceedLimit_Passes()
    {
        // Old calls outside 60s window should not count.
        var state = WithSteps(OldStep(600, 600));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_MixedOldAndRecent_OnlyCountsRecent()
    {
        // Old step: 800 tokens (outside window), recent step: 400 tokens → 400 in window, below 1000
        var state = WithSteps(OldStep(500, 300), RecentStep(200, 200));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_CacheReadsExcludedFromLimit_Passes()
    {
        // 5,000-token prompt, 4,800 of it served from cache. Billed and carried in context in full, but only the
        // 200 uncached tokens count toward ITPM — 300 with output, comfortably under the limit. Counting the whole
        // prompt (the old behaviour) reads 5,100 and throttles a healthy agent 5x too early.
        var state = WithSteps(CachedStep(inputTokens: 5_000, outputTokens: 100, cacheReadTokens: 4_800));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.False(result.IsLimited);
    }

    [Fact]
    public async Task Check_UncachedPortionStillCounts_IsLimited()
    {
        // The exclusion is for cache reads only — a genuinely large uncached prompt still throttles.
        var state = WithSteps(CachedStep(inputTokens: 5_000, outputTokens: 100, cacheReadTokens: 1_000));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_ProviderDidNotDeclareAccounting_CountsFullPromptAndIsLimited()
    {
        // InputTokensTowardRateLimit is null here (ModelStep leaves it unset), so the limiter counts the whole
        // prompt. Being wrong in this direction throttles early, never late.
        var state = WithSteps(RecentStep(5_000, 100));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.IsLimited);
    }

    [Fact]
    public async Task Check_WhenLimited_RetryAfterIsPositive()
    {
        var state = WithSteps(RecentStep(600, 400));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.True(result.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Check_WhenLimited_ReasonMentionsTokenCount()
    {
        var state = WithSteps(RecentStep(600, 400));
        var result = await Sut.CheckAsync(state, CancellationToken.None);
        Assert.Contains("1,000", result.Reason);
    }

    // ── RetryAfter accuracy (controlled clock) ────────────────────────────────

    [Fact]
    public async Task Check_OneEvictionIsNotEnough_RetryAfterCoversEveryEntryItMustOutlast()
    {
        // The window is a token SUM, so ageing out the single oldest call need not clear it — the
        // sibling calls-per-minute limiter is shielded only because its count drops by exactly one.
        // Here dropping A still leaves 1,489 against a 1,000 ceiling, so reporting A's age-out time
        // sends the loop back a second early and the tail degenerates into 1s busy-polls, each
        // carrying a checkpoint write.
        var sut = new TokensPerMinuteRateLimiter(1_000, new FixedClock(T0.AddSeconds(30)));
        var state = WithSteps(
            At(T0, 10),                  // ages out at T0+60 → 1,489 left, still over the limit
            At(T0.AddSeconds(1), 989),   // ages out at T0+61 → 500 left, under
            At(T0.AddSeconds(2), 500));

        var result = await sut.CheckAsync(state, CancellationToken.None);

        Assert.True(result.IsLimited);
        Assert.Equal(TimeSpan.FromSeconds(31), result.RetryAfter); // (T0+1s)+60s − (T0+30s)
    }

    [Fact]
    public async Task Check_OneEvictionClearsIt_RetryAfterDoesNotOverWait()
    {
        // The mirror of the above: when the oldest entry alone brings the sum under the ceiling, the
        // wait must stay at its age-out time rather than reaching for later entries.
        var sut = new TokensPerMinuteRateLimiter(1_000, new FixedClock(T0.AddSeconds(30)));
        var state = WithSteps(At(T0, 800), At(T0.AddSeconds(5), 300));

        var result = await sut.CheckAsync(state, CancellationToken.None);

        Assert.True(result.IsLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter); // T0+60s − (T0+30s)
    }

    [Fact]
    public async Task Check_CallExactlySixtySecondsOld_HasAgedOutOfTheWindow()
    {
        // Exclusive lower bound: RetryAfter targets exactly this instant, so counting the call at it
        // would mean a correctly-sized wait never clears the limit.
        var sut = new TokensPerMinuteRateLimiter(1_000, new FixedClock(T0.AddSeconds(60)));

        var result = await sut.CheckAsync(WithSteps(At(T0, 5_000)), CancellationToken.None);

        Assert.False(result.IsLimited);
    }

    [Fact]
    public void Ctor_NonPositiveLimit_Throws()
    {
        // Without this the pass-guard is false for an empty window and the RetryAfter path indexes an
        // empty list — a misconfiguration surfacing as an IndexOutOfRangeException mid-run.
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokensPerMinuteRateLimiter(0));
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ModelCallStep At(DateTimeOffset timestamp, int inputTokens) =>
        ModelStep(timestamp, inputTokens, outputTokens: 0);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
