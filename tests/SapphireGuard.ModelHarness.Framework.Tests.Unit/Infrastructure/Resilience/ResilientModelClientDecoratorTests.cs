using Polly.CircuitBreaker;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Resilience;

public sealed class ResilientModelClientDecoratorTests
{
    // The default breaker's MinimumThroughput: this many failed calls in a row open the circuit.
    private const int CallsToOpen = 4;

    private static readonly ModelResponse Ok = new()
    {
        Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m
    };

    // Regression guard for the breaker-only change: the decorator must NOT retry the inner
    // client. The provider SDKs already retry transient failures; a second Polly retry layer
    // stacked on top caused compounding back-off / retry storms under rate limits.
    [Fact]
    public async Task CallAsync_InnerThrows_DoesNotRetry_InnerCalledExactlyOnce()
    {
        var inner = new CountingThrowingClient();
        var decorator = new ResilientModelClientDecorator(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decorator.CallAsync([], [], CancellationToken.None));

        Assert.Equal(1, inner.CallCount);
    }

    // A provider that hangs until the client's own request timeout elapses is the outage the
    // breaker exists for. HttpClient reports that timeout as TaskCanceledException — an
    // OperationCanceledException — with the caller's token untouched, so it must count as a failure.
    [Fact]
    public async Task CallAsync_InnerRepeatedlyTimesOutOnItsOwn_OpensTheCircuit()
    {
        var inner = new StubClient(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        var decorator = new ResilientModelClientDecorator(inner);

        for (var i = 0; i < CallsToOpen; i++)
            await Assert.ThrowsAsync<TaskCanceledException>(() => decorator.CallAsync([], [], CancellationToken.None));

        await Assert.ThrowsAsync<BrokenCircuitException>(() => decorator.CallAsync([], [], CancellationToken.None));
        Assert.Equal(CallsToOpen, inner.CallCount);
    }

    [Fact]
    public async Task CallAsync_CallerCancelsRepeatedly_DoesNotOpenTheCircuit()
    {
        // Genuine cancellation is the caller's decision, not a provider fault.
        var inner = new StubClient(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Ok;
        });
        var decorator = new ResilientModelClientDecorator(inner);

        for (var i = 0; i < CallsToOpen; i++)
        {
            using var cts = new CancellationTokenSource();
            var call = decorator.CallAsync([], [], cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        Assert.Equal(CallsToOpen, inner.CallCount);

        inner.Respond = _ => Task.FromResult(Ok);
        Assert.Same(Ok, await decorator.CallAsync([], [], CancellationToken.None));
    }

    [Fact]
    public async Task CallAsync_OneClientKeepsFailing_DoesNotOpenAnotherClientsCircuit()
    {
        // A breaker's failure history belongs to the one client it guards: an outage at one
        // provider must not fast-fail calls to another, or to a sub-agent's client.
        var failing = new ResilientModelClientDecorator(
            new StubClient(_ => throw new InvalidOperationException("provider down")));
        for (var i = 0; i < CallsToOpen; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CallAsync([], [], CancellationToken.None));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => failing.CallAsync([], [], CancellationToken.None));

        var healthy = new ResilientModelClientDecorator(new StubClient(_ => Task.FromResult(Ok)));

        Assert.Same(Ok, await healthy.CallAsync([], [], CancellationToken.None));
    }

    private sealed class StubClient(Func<CancellationToken, Task<ModelResponse>> respond) : IModelClient
    {
        public Func<CancellationToken, Task<ModelResponse>> Respond { get; set; } = respond;
        public int CallCount { get; private set; }

        public Task<ModelResponse> CallAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ToolDefinition> availableTools,
            CancellationToken ct)
        {
            CallCount++;
            return Respond(ct);
        }
    }

    private sealed class CountingThrowingClient : IModelClient
    {
        public int CallCount { get; private set; }

        public async Task<ModelResponse> CallAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ToolDefinition> availableTools,
            CancellationToken ct)
        {
            CallCount++;
            await Task.Yield();
            throw new InvalidOperationException("transient");
        }
    }
}
