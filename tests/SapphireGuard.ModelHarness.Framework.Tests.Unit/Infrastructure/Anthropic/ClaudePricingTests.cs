using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudePricingTests
{
    private const long OneMillion = 1_000_000;

    public static TheoryData<string?, decimal, decimal, decimal> PublishedPrices => new()
    {
        { "claude-haiku-4-5-20251001", 1.00m, 5.00m, 0.10m },
        { "claude-sonnet-4-6", 3.00m, 15.00m, 0.30m },
        { "claude-sonnet-5", 2.00m, 10.00m, 0.20m },
        { "claude-sonnet-5-5", 2.00m, 10.00m, 0.10m },
        { "claude-opus-4-8", 5.00m, 25.00m, 0.50m },
        { "claude-opus-5", 5.00m, 25.00m, 0.50m },
        { "claude-opus-5-5", 4.00m, 20.00m, 0.20m },
        { "claude-fable-5", 10.00m, 50.00m, 1.00m },
        { "claude-fable-5-1", 10.00m, 50.00m, 0.25m },
        { "claude-unreleased-9", 10.00m, 50.00m, 1.00m },
        { null, 10.00m, 50.00m, 1.00m },
    };

    [Theory]
    [MemberData(nameof(PublishedPrices))]
    public void Cost_ModelId_BillsEachBucketAtThatModelsRate(
        string? modelId, decimal inputPer1M, decimal outputPer1M, decimal cacheReadPer1M)
    {
        Assert.Equal(inputPer1M, ClaudePricing.Cost(modelId, OneMillion, 0, 0, 0));
        Assert.Equal(outputPer1M, ClaudePricing.Cost(modelId, 0, 0, 0, OneMillion));
        Assert.Equal(cacheReadPer1M, ClaudePricing.Cost(modelId, 0, OneMillion, 0, 0));
        Assert.Equal(inputPer1M * 1.25m, ClaudePricing.Cost(modelId, 0, 0, OneMillion, 0));
    }

    [Fact]
    public void Cost_AllBuckets_SumsThem()
    {
        var cost = ClaudePricing.Cost("claude-haiku-4-5", 2_000, 10_000, 4_000, 1_000);

        Assert.Equal(0.002m + 0.001m + 0.005m + 0.005m, cost);
    }

    // Haiku 5.5 is priced by prompt length. The theory above can't pin it: a million tokens in one input bucket is
    // itself a long prompt, while output alone has no prompt at all.
    [Fact]
    public void Cost_Haiku55_PromptOfExactly100KTokens_BillsTheStandardRates()
    {
        // Prompt = 60k uncached + 30k cache reads + 10k cache writes = 100,000 — not *over* the threshold.
        var cost = ClaudePricing.Cost("claude-haiku-5-5", 60_000, 30_000, 10_000, 1_000);

        Assert.Equal((60_000 * 0.10m + 30_000 * 0.01m + 10_000 * 0.10m * 1.25m + 1_000 * 0.50m) / 1_000_000m, cost);
    }

    [Fact]
    public void Cost_Haiku55_PromptOver100KCountingCacheTraffic_BillsEveryBucketAtTheHigherRates()
    {
        // One token over, reached only by counting the cache reads and writes — and output moves tier too.
        var cost = ClaudePricing.Cost("claude-haiku-5-5", 60_001, 30_000, 10_000, 1_000);

        Assert.Equal((60_001 * 0.50m + 30_000 * 0.05m + 10_000 * 0.50m * 1.25m + 1_000 * 2.50m) / 1_000_000m, cost);
    }
}
