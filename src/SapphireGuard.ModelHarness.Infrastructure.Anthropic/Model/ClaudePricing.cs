namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

// First-party Claude API prices in USD per million tokens, from https://platform.claude.com/docs/en/about-claude/pricing
// (checked 2026-10-11). Prices change — update the table when they do. It lists every model that page shows the API
// still serving; retired models cannot arrive through ClaudeModelClient.
internal static class ClaudePricing
{
    // AbovePromptThreshold: what a request pays instead once its prompt exceeds LongPromptThreshold (Haiku 5.5 only).
    private sealed record Price(decimal Input, decimal Output, decimal CacheRead, Price? AbovePromptThreshold = null);

    // Matched by longest prefix, so a dated id (claude-haiku-4-5-20251001) finds its model and claude-opus-5-5 is
    // not priced as claude-opus-5. Cache reads are listed per model: 0.1x input on most, less on the newest.
    private static readonly (string ModelPrefix, Price Price)[] Prices =
    [
        ("claude-fable-5-1", new(10.00m, 50.00m, 0.25m)),
        ("claude-mythos-5-1", new(10.00m, 50.00m, 0.25m)),
        ("claude-fable-5", new(10.00m, 50.00m, 1.00m)),
        ("claude-mythos-5", new(10.00m, 50.00m, 1.00m)),
        ("claude-opus-5-5", new(4.00m, 20.00m, 0.20m)),
        ("claude-opus-5", new(5.00m, 25.00m, 0.50m)),
        ("claude-opus-4-8", new(5.00m, 25.00m, 0.50m)),
        ("claude-opus-4-7", new(5.00m, 25.00m, 0.50m)),
        ("claude-opus-4-6", new(5.00m, 25.00m, 0.50m)),
        ("claude-opus-4-5", new(5.00m, 25.00m, 0.50m)),
        ("claude-sonnet-5-5", new(2.00m, 10.00m, 0.10m)),
        ("claude-sonnet-5", new(2.00m, 10.00m, 0.20m)),
        ("claude-sonnet-4-6", new(3.00m, 15.00m, 0.30m)),
        ("claude-sonnet-4-5", new(3.00m, 15.00m, 0.30m)),
        ("claude-haiku-5-5", new(0.10m, 0.50m, 0.01m, AbovePromptThreshold: new(0.50m, 2.50m, 0.05m))),
        ("claude-haiku-4-5", new(1.00m, 5.00m, 0.10m)),
    ];

    // An id missing from the table is priced at the top tier: over-reporting stops a MaxCost budget early, where
    // under-reporting would let a run overspend unnoticed.
    private static readonly Price Unknown = new(10.00m, 50.00m, 1.00m);

    // ClaudeModelClient only writes the 5-minute cache; a 1-hour write would bill at 2x.
    private const decimal CacheWriteMultiplier = 1.25m;

    // A prompt over this many tokens — every input bucket, cache reads and writes included — moves the whole request,
    // output too, to its model's AbovePromptThreshold prices. Each request is priced on its own.
    private const long LongPromptThreshold = 100_000;

    internal static decimal Cost(
        string? modelId, long uncachedInput, long cacheReadTokens, long cacheWriteTokens, long outputTokens)
    {
        var price = PriceFor(modelId);
        if (price.AbovePromptThreshold is { } longPrompt
            && uncachedInput + cacheReadTokens + cacheWriteTokens > LongPromptThreshold)
            price = longPrompt;

        return (uncachedInput * price.Input
              + cacheReadTokens * price.CacheRead
              + cacheWriteTokens * price.Input * CacheWriteMultiplier
              + outputTokens * price.Output) / 1_000_000m;
    }

    private static Price PriceFor(string? modelId) =>
        Prices
            .Where(p => modelId?.StartsWith(p.ModelPrefix, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(p => p.ModelPrefix.Length)
            .Select(p => p.Price)
            .FirstOrDefault() ?? Unknown;
}
