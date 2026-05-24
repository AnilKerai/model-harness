namespace SapphireGuard.ModelHarness.Framework.RateLimiting;

public sealed record RateLimitCheck(bool IsLimited, TimeSpan? RetryAfter, string? Reason)
{
    public static RateLimitCheck Pass { get; } = new(false, null, null);

    public static RateLimitCheck Limited(TimeSpan retryAfter, string reason) =>
        new(true, retryAfter, reason);
}
