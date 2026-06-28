using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.RateLimiting;

public sealed class CallsPerMinuteRateLimiter(int callsPerMinute, TimeProvider? timeProvider = null) : IRateLimiter
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

        if (recent.Count < callsPerMinute)
            return Task.FromResult(RateLimitCheck.Pass);

        var retryAfter = recent[0].Timestamp + TimeSpan.FromMinutes(1) - now;
        return Task.FromResult(RateLimitCheck.Limited(
            retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1),
            $"Rate limit: {recent.Count} calls in the last 60s exceeds the {callsPerMinute} calls/min limit."));
    }
}
