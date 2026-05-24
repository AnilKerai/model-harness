using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.RateLimiting;

[ExcludeFromCodeCoverage]
public sealed class NullRateLimiter : IRateLimiter
{
    public Task<RateLimitCheck> CheckAsync(AgentState state, CancellationToken ct) =>
        Task.FromResult(RateLimitCheck.Pass);
}
