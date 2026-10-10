using System.Text.Json;
using Polly.CircuitBreaker;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Resilience;

public sealed class ResilientToolTests
{
    // The default breaker's MinimumThroughput: this many failed attempts in a row open the circuit.
    private const int AttemptsToOpen = 4;

    private const string HttpTimeout =
        "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.";

    private static ToolCall Call() => new("call-1", "stub", JsonDocument.Parse("{}").RootElement);

    private static Task<ToolResult> Execute(ResilientTool tool, CancellationToken ct = default) =>
        tool.ExecuteAsync(Call(), ToolContext.Empty("task-1", "call-1"), ct);

    // A backend that hangs until the tool's own HttpClient timeout elapses raises
    // TaskCanceledException — an OperationCanceledException — with the caller's token untouched.
    // It is a failed call, so the breaker must count it.
    [Fact]
    public async Task ExecuteAsync_ToolRepeatedlyTimesOutOnItsOwn_OpensTheCircuit()
    {
        var inner = new StubTool(_ => throw new TaskCanceledException(HttpTimeout, new TimeoutException()));
        var tool = new ResilientTool(inner);

        for (var i = 0; i < AttemptsToOpen; i++)
            await Assert.ThrowsAsync<TaskCanceledException>(() => Execute(tool));

        await Assert.ThrowsAsync<BrokenCircuitException>(() => Execute(tool));
        Assert.Equal(AttemptsToOpen, inner.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ToolTimesOutOnItsOwn_IsNotRetried()
    {
        // Deliberate: a call that timed out may already have run its side effect, and each retry
        // would wait out the full timeout again. The breaker counts it; the retry leaves it alone.
        var inner = new StubTool(_ => throw new TaskCanceledException(HttpTimeout, new TimeoutException()));
        var tool = new ResilientTool(inner);

        await Assert.ThrowsAsync<TaskCanceledException>(() => Execute(tool));

        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancelsRepeatedly_DoesNotOpenTheCircuit()
    {
        // Covers the harness's own per-tool deadline too: it cancels the token the tool is given.
        var inner = new StubTool(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok();
        });
        var tool = new ResilientTool(inner);

        for (var i = 0; i < AttemptsToOpen; i++)
        {
            using var cts = new CancellationTokenSource();
            var call = Execute(tool, cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        Assert.Equal(AttemptsToOpen, inner.CallCount);

        inner.Respond = _ => Task.FromResult(Ok());
        Assert.Equal("ok", (await Execute(tool)).Content);
    }

    [Fact]
    public async Task ExecuteAsync_OneToolKeepsFailing_DoesNotOpenAnotherToolsCircuit()
    {
        // A breaker's failure history belongs to the one tool it guards. Shared, a single failed
        // invocation (one attempt plus three retries) fast-failed every other resilient tool.
        var failingInner = new StubTool(_ => throw new HttpRequestException("backend down"));
        var failing = new ResilientTool(failingInner);
        await Assert.ThrowsAsync<HttpRequestException>(() => Execute(failing));
        Assert.Equal(AttemptsToOpen, failingInner.CallCount);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => Execute(failing));

        var healthy = new ResilientTool(new StubTool(_ => Task.FromResult(Ok())));

        Assert.Equal("ok", (await Execute(healthy)).Content);
    }

    private static ToolResult Ok() => new("call-1", "ok");

    private sealed class StubTool(Func<CancellationToken, Task<ToolResult>> respond) : ITool
    {
        public Func<CancellationToken, Task<ToolResult>> Respond { get; set; } = respond;
        public int CallCount { get; private set; }

        public string Name => "stub";
        public string Description => "stub";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            CallCount++;
            return Respond(ct);
        }
    }
}
