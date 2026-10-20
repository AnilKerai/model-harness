using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudeClientOptionsTests
{
    // "claude-sonnet-4-5-20250929" was checked against the live Anthropic API on 2026-10-20. This test pins the
    // string but cannot tell a real id from a wrong date suffix: the previous default, "claude-sonnet-4-5-20251001"
    // (Haiku 4.5's date on the Sonnet name), passed every offline check and 404ed on every call. Re-check an
    // edited id live.
    private const string DefaultModelId = "claude-sonnet-4-5-20250929";

    [Fact]
    public void ModelId_NotSet_IsTheDatedSonnet45Snapshot()
    {
        var options = new ClaudeClientOptions { ApiKey = "test-key" };

        Assert.Equal(DefaultModelId, options.ModelId);
    }

    [Fact]
    public void ModelId_Default_HasAPriceInTheTable()
    {
        // An id missing from the table is priced at the top tier ($10 / $50 per million), not Sonnet 4.5's $3 / $15.
        var cost = ClaudePricing.Cost(new ClaudeClientOptions { ApiKey = "test-key" }.ModelId, 1_000_000, 0, 0, 1_000_000);

        Assert.Equal(3.00m + 15.00m, cost);
    }
}
