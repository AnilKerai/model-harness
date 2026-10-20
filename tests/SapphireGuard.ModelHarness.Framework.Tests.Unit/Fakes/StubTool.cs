using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

/// <summary>A tool that exists only to be listed; <see cref="StubToolRegistry"/> does the dispatching.</summary>
public sealed class StubTool(string name) : ITool
{
    public string Name => name;
    public string Description => "stub";
    public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        throw new NotSupportedException("StubToolRegistry dispatches by itself; it never calls ITool.ExecuteAsync.");
}
