using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Resilience;

public sealed class ResilientModelClientDecoratorTests
{
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
