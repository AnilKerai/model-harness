using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

public sealed class StubToolRegistry : IToolRegistry
{
    private readonly Func<ToolCall, ToolResult>? _dispatch;
    private int _dispatchCount;

    public int DispatchCount => _dispatchCount;

    public StubToolRegistry(Func<ToolCall, ToolResult>? dispatch = null)
    {
        _dispatch = dispatch;
    }

    public IReadOnlyList<ITool> List() => [];
    public ITool? Get(string name) => null;

    public Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref _dispatchCount);
        var result = _dispatch?.Invoke(call) ?? new ToolResult(call.CallId, "stub-result");
        return Task.FromResult(result);
    }
}
