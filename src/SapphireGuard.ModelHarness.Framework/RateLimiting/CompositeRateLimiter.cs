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
            if (mostRestrictive is null || check.RetryAfter > mostRestrictive.RetryAfter)
                mostRestrictive = check;
        }
        return mostRestrictive ?? RateLimitCheck.Pass;
    }
}
