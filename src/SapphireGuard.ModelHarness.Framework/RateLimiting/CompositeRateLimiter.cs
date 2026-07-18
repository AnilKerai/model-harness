using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.RateLimiting;

public sealed class CompositeRateLimiter(IRateLimiter[] limiters) : IRateLimiter
{
    public async Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct)
    {
        RateLimitCheck? mostRestrictive = null;
        foreach (var limiter in limiters)
        {
            var check = await limiter.CheckAsync(state, ct);
            if (!check.IsLimited) continue;
            if (mostRestrictive is null || IsLongerWait(check.RetryAfter, mostRestrictive.RetryAfter))
                mostRestrictive = check;
        }
        return mostRestrictive ?? RateLimitCheck.Pass;
    }

    // A null RetryAfter means "limited, duration unknown" — the weakest claim, so any limiter that
    // does report a wait must beat it. A plain lifted `>` is false whenever either side is null, which
    // silently kept the first null and discarded a real wait behind it; the loop then fell back to its
    // 10s default and under-waited. Built-in limiters always report non-null, so this only bites a
    // custom IRateLimiter.
    private static bool IsLongerWait(TimeSpan? candidate, TimeSpan? incumbent) =>
        candidate is not null && (incumbent is null || candidate > incumbent);
}
