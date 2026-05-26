using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.RateLimiting;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.RateLimiting;

public sealed class TokensPerMinuteRateLimiterTests
{
    private static readonly TokensPerMinuteRateLimiter Sut = new(tokensPerMinute: 1_000);

    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 100, MaxContextTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(5)
    });

    private static ModelCallStep RecentStep(int inputTokens, int outputTokens) =>
        ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10), inputTokens, outputTokens);

    private static ModelCallStep OldStep(int inputTokens, int outputTokens) =>
        ModelStep(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2), inputTokens, outputTokens);

    private static ModelCallStep ModelStep(DateTimeOffset timestamp, int inputTokens, int outputTokens) =>
        new(Guid.NewGuid(), timestamp,
            Prompt: [],
            Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = new Usage(inputTokens, outputTokens), Cost = 0m },
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
}
