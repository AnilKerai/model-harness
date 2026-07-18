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

    private readonly int _tokensPerMinute = tokensPerMinute > 0
        ? tokensPerMinute
        : throw new ArgumentOutOfRangeException(nameof(tokensPerMinute), tokensPerMinute, "Must be greater than zero.");

    public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var windowStart = now - TimeSpan.FromMinutes(1);

        // Exclusive lower bound: RetryAfter targets exactly the moment a call ages out, and an inclusive
        // bound would still count that call at that instant, so the wait always landed one tick short.
        var recent = state.Trajectory
            .OfType<ModelCallStep>()
            .Where(s => s.Timestamp > windowStart)
            .OrderBy(s => s.Timestamp)
            .ToList();

        // ponytail: one combined budget, where providers meter input and output separately (Anthropic enforces ITPM
        // and OTPM as distinct limits). Summing them against a single ceiling only ever throttles early, so set the
        // ceiling from the input limit. Split into two windows if output volume ever needs headroom of its own.
        var totalTokens = recent.Sum(RateLimitedTokens);
        if (totalTokens < _tokensPerMinute)
            return Task.FromResult(RateLimitCheck.Pass);

        return Task.FromResult(RateLimitCheck.Limited(
            TimeUntilUnderLimit(recent, totalTokens, now),
            $"Rate limit: {totalTokens:N0} tokens in the last 60s exceeds the {_tokensPerMinute:N0} tokens/min limit."));
    }

    // The window is a token SUM, so ageing out the single oldest call need not clear it — unlike the
    // sibling calls-per-minute limiter, where evicting one entry always drops the count by exactly one.
    // Age entries out oldest-first until the remainder is under the ceiling and wait for that one, or
    // the reported wait is far too short and the loop's re-check tail degenerates into 1s busy-polls,
    // each carrying a checkpoint save.
    private TimeSpan TimeUntilUnderLimit(List<ModelCallStep> recent, int totalTokens, DateTimeOffset now)
    {
        var remaining = totalTokens;
        var last = 0;
        for (var i = 0; i < recent.Count; i++)
        {
            remaining -= RateLimitedTokens(recent[i]);
            last = i;
            if (remaining < _tokensPerMinute)
                break;
        }

        var retryAfter = recent[last].Timestamp + TimeSpan.FromMinutes(1) - now;
        return retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1);
    }

    private static int RateLimitedTokens(ModelCallStep step) =>
        (step.Response.InputTokensTowardRateLimit ?? step.Usage.InputTokens) + step.Usage.OutputTokens;
}
