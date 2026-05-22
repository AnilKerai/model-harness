using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tools;

public sealed class InMemoryToolRegistryTests
{
    private static ToolCall Call(string toolName, string argsJson = "{}") =>
        new(Guid.NewGuid().ToString("n"), toolName, JsonDocument.Parse(argsJson).RootElement);

    private static ToolContext Ctx() =>
        ToolContext.Empty(Guid.NewGuid().ToString("n"), Guid.NewGuid().ToString("n"));

    [Fact]
    public void List_ReturnsAllToolsInRegistrationOrder()
    {
        var a = new StubTool("a");
        var b = new StubTool("b");
        var registry = new InMemoryToolRegistry([a, b]);

        var list = registry.List();

        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].Name);
        Assert.Equal("b", list[1].Name);
    }

    [Fact]
    public void Get_KnownTool_ReturnsTool()
    {
        var tool = new StubTool("calc");
        var registry = new InMemoryToolRegistry([tool]);

        Assert.Same(tool, registry.Get("calc"));
    }

    [Fact]
    public void Get_UnknownTool_ReturnsNull()
    {
        var registry = new InMemoryToolRegistry([]);
        Assert.Null(registry.Get("nope"));
    }

    [Fact]
    public async Task DispatchAsync_KnownTool_ReturnsToolResult()
    {
        var tool = new StubTool("echo", result: "hello");
        var registry = new InMemoryToolRegistry([tool]);

        var result = await registry.DispatchAsync(Call("echo"), Ctx(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_ReturnsError()
    {
        var registry = new InMemoryToolRegistry([]);

        var result = await registry.DispatchAsync(Call("missing"), Ctx(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("missing", result.Content);
    }

    [Fact]
    public async Task DispatchAsync_ToolThrows_ReturnsErrorResult()
    {
        var tool = new ThrowingTool("explode");
        var registry = new InMemoryToolRegistry([tool]);

        var result = await registry.DispatchAsync(Call("explode"), Ctx(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("something went wrong", result.Content);
    }

    [Fact]
    public async Task DispatchAsync_ToolThrowsOperationCanceled_Rethrows()
    {
        var tool = new CancellingTool("cancel");
        var registry = new InMemoryToolRegistry([tool]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => registry.DispatchAsync(Call("cancel"), Ctx(), CancellationToken.None));
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

file sealed class StubTool(string name, string result = "ok") : ITool
{
    public string Name => name;
    public string Description => name;
    public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;
    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, result));
}

file sealed class ThrowingTool(string name) : ITool
{
    public string Name => name;
    public string Description => name;
    public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;
    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => throw new InvalidOperationException("something went wrong");
}

file sealed class CancellingTool(string name) : ITool
{
    public string Name => name;
    public string Description => name;
    public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;
    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => throw new OperationCanceledException();
}
