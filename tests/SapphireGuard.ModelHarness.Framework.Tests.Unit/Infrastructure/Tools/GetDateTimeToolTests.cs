using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tools;

public sealed class GetDateTimeToolTests
{
    private static readonly ToolContext Ctx =
        ToolContext.Empty(Guid.NewGuid().ToString("n"), Guid.NewGuid().ToString("n"));

    private static ToolCall EmptyCall() =>
        new(Guid.NewGuid().ToString("n"), "get_date_time", JsonDocument.Parse("{}").RootElement);

    [Fact]
    public async Task Execute_ReturnsIso8601UtcString()
    {
        var sut = new GetDateTimeTool(new StubTimeProvider(new DateTimeOffset(2026, 6, 6, 14, 30, 0, TimeSpan.Zero)));

        var result = await sut.ExecuteAsync(EmptyCall(), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("2026-06-06T14:30:00Z", result.Content);
    }

    [Fact]
    public async Task Execute_NormalisesNonUtcOffsetToUtc()
    {
        var bst = new DateTimeOffset(2026, 6, 6, 15, 30, 0, TimeSpan.FromHours(1));
        var sut = new GetDateTimeTool(new StubTimeProvider(bst));

        var result = await sut.ExecuteAsync(EmptyCall(), Ctx, CancellationToken.None);

        Assert.Equal("2026-06-06T14:30:00Z", result.Content);
    }

    [Fact]
    public async Task Execute_SystemTimeProvider_ReturnsCurrentUtcTime()
    {
        var before = DateTime.UtcNow;
        var sut = new GetDateTimeTool(TimeProvider.System);

        var result = await sut.ExecuteAsync(EmptyCall(), Ctx, CancellationToken.None);
        var after = DateTime.UtcNow;

        Assert.False(result.IsError);
        var parsed = DateTime.Parse(result.Content, null, System.Globalization.DateTimeStyles.RoundtripKind);
        // "s" format truncates sub-seconds, so allow one second below before
        Assert.InRange(parsed, before.AddSeconds(-1), after);
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
