using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.RateLimiting;

public interface IRateLimiter
{
    Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct);
}
