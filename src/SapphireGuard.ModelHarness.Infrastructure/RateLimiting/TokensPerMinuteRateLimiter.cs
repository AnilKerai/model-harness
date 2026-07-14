using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.RateLimiting;

/// <summary>
/// Throttles when the model calls in the last 60 seconds exceed the given tokens-per-minute ceiling.
/// <para>
/// Counts each call's <see cref="ModelResponse.InputTokensTowardRateLimit"/> rather than its billed input, because the
/// two diverge under prompt caching: Anthropic excludes cache reads from ITPM, so a cached prefix costs money and
/// context but almost no rate-limit budget. A client that has not declared its provider's accounting reports null, and
/// the full prompt is counted instead — that throttles early rather than late, which is the safe way to be wrong.
/// </para>
/// </summary>
public sealed class TokensPerMinuteRateLimiter(int tokensPerMinute, TimeProvider? timeProvider = null) : IRateLimiter
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var windowStart = now - TimeSpan.FromMinutes(1);

        var recent = state.Trajectory
            .OfType<ModelCallStep>()
            .Where(s => s.Timestamp >= windowStart)
            .OrderBy(s => s.Timestamp)
            .ToList();

        // ponytail: one combined budget, where providers meter input and output separately (Anthropic enforces ITPM
        // and OTPM as distinct limits). Summing them against a single ceiling only ever throttles early, so set the
        // ceiling from the input limit. Split into two windows if output volume ever needs headroom of its own.
        var totalTokens = recent.Sum(RateLimitedTokens);
        if (totalTokens < tokensPerMinute)
            return Task.FromResult(RateLimitCheck.Pass);

        var retryAfter = recent[0].Timestamp + TimeSpan.FromMinutes(1) - now;
        return Task.FromResult(RateLimitCheck.Limited(
            retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1),
            $"Rate limit: {totalTokens:N0} tokens in the last 60s exceeds the {tokensPerMinute:N0} tokens/min limit."));
    }

    private static int RateLimitedTokens(ModelCallStep step) =>
        (step.Response.InputTokensTowardRateLimit ?? step.Usage.InputTokens) + step.Usage.OutputTokens;
}
