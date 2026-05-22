using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tools;

public sealed class CalculatorToolTests
{
    private static readonly CalculatorTool Sut = new();
    private static readonly ToolContext Ctx =
        ToolContext.Empty(Guid.NewGuid().ToString("n"), Guid.NewGuid().ToString("n"));

    private static ToolCall Call(string op, double lhs, double rhs)
    {
        var json = $$"""{"op":"{{op}}","lhs":{{lhs}},"rhs":{{rhs}}}""";
        return new ToolCall(Guid.NewGuid().ToString("n"), "calculator", JsonDocument.Parse(json).RootElement);
    }

    [Theory]
    [InlineData("add", 3, 4, "7")]
    [InlineData("sub", 10, 3, "7")]
    [InlineData("mul", 3, 4, "12")]
    [InlineData("div", 10, 2, "5")]
    public async Task Execute_ValidOperation_ReturnsCorrectResult(string op, double lhs, double rhs, string expected)
    {
        var result = await Sut.ExecuteAsync(Call(op, lhs, rhs), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task Execute_DivisionByZero_ReturnsError()
    {
        var result = await Sut.ExecuteAsync(Call("div", 10, 0), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("zero", result.Content);
    }

    [Fact]
    public async Task Execute_InvalidOp_ReturnsError()
    {
        var result = await Sut.ExecuteAsync(Call("pow", 2, 3), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("pow", result.Content);
    }

    [Fact]
    public async Task Execute_FloatingPointOp_ReturnsCorrectResult()
    {
        var result = await Sut.ExecuteAsync(Call("div", 1, 3), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("0.333", result.Content);
    }
}
