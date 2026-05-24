using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Tools;

[ExcludeFromCodeCoverage]
public sealed class NullToolRegistry : IToolRegistry
{
    public IReadOnlyList<ITool> List() => [];

    public ITool? Get(string name) => null;

    public Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        Task.FromResult(new ToolResult(call.CallId, "No tool registry is configured.", IsError: true));
}
