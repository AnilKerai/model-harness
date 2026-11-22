using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudeClientOptionsTests
{
    // "claude-sonnet-5-5" was checked against the live GET /v1/models on 2026-11-01. This test pins the
    // string but cannot tell a real id from a wrong one: an earlier default (Haiku 4.5's date suffix on
    // the Sonnet 4.5 name) passed every offline check and 404ed on every call. Re-check an edited id live.
    private const string DefaultModelId = "claude-sonnet-5-5";

    [Fact]
    public void ModelId_NotSet_IsSonnet55()
    {
        var options = new ClaudeClientOptions { ApiKey = "test-key" };

        Assert.Equal(DefaultModelId, options.ModelId);
    }

    [Fact]
    public void ModelId_Default_HasAPriceInTheTable()
    {
        // An id missing from the table is priced at the top tier ($10 / $50 per million), not Sonnet 5.5's $2 / $10.
        var cost = ClaudePricing.Cost(new ClaudeClientOptions { ApiKey = "test-key" }.ModelId, 1_000_000, 0, 0, 1_000_000);

        Assert.Equal(2.00m + 10.00m, cost);
    }
}
