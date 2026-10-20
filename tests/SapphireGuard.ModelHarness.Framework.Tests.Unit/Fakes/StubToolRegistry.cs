using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

/// <summary>
/// Lists exactly the tools it was given and dispatches only those, as a real registry does. The loop
/// offers the model what <see cref="List"/> returns and refuses calls to anything else, so a test that
/// makes the model call a tool must name that tool here.
/// </summary>
public sealed class StubToolRegistry : IToolRegistry
{
    private readonly Func<ToolCall, ToolResult>? _dispatch;
    private readonly ITool[] _tools;
    private int _dispatchCount;

    public int DispatchCount => _dispatchCount;

    public ToolContext? CapturedContext { get; private set; }

    public StubToolRegistry(params string[] toolNames)
        : this(null, toolNames)
    {
    }

    public StubToolRegistry(Func<ToolCall, ToolResult>? dispatch, params string[] toolNames)
    {
        _dispatch = dispatch;
        _tools = [.. toolNames.Select(n => new StubTool(n))];
    }

    public IReadOnlyList<ITool> List() => _tools;
    public ITool? Get(string name) => _tools.FirstOrDefault(t => t.Name == name);

    public Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        if (Get(call.ToolName) is null)
            return Task.FromResult(new ToolResult(call.CallId, $"Unknown tool '{call.ToolName}'.", IsError: true));

        CapturedContext = context;
        Interlocked.Increment(ref _dispatchCount);
        var result = _dispatch?.Invoke(call) ?? new ToolResult(call.CallId, "stub-result");
        return Task.FromResult(result);
    }
}
