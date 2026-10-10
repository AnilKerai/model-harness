using Polly;
using Polly.CircuitBreaker;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

/// <summary>
/// Wraps an <see cref="IModelClient"/> with a Polly circuit breaker so a sustained
/// provider outage fast-fails for a cool-off window instead of hammering a dead endpoint
/// every turn.
/// </summary>
/// <remarks>
/// It deliberately does <b>not</b> retry or impose a timeout. The official provider SDKs
/// (Anthropic, Azure OpenAI) already retry transient failures (connection errors, 408/409/429/5xx)
/// with exponential back-off and apply a request timeout; stacking a second retry layer here caused
/// compounding back-off and retry storms under rate limits. Tune retry count and timeout via the
/// adapter's <c>ConfigureClient</c> hook instead (e.g. <c>ConfigureClient = o =&gt; o.Timeout = ...</c>);
/// this decorator adds only the circuit breaker the SDKs lack. The default breaker counts every
/// failure surfacing from the inner client except the caller cancelling the call (provider SDKs raise
/// their own exception types, so a transport-only predicate would never fire). The client's own
/// request timeout counts: it surfaces as an <see cref="OperationCanceledException"/>, but with the
/// caller's token untouched. Each decorator built without a <see cref="ResiliencePipeline{T}"/> gets
/// its own breaker, so one client's outage never fast-fails another's calls; pass the same pipeline
/// to several decorators to share one deliberately, or supply your own to narrow what it counts.
/// </remarks>
public sealed class ResilientModelClientDecorator(
    IModelClient inner,
    ResiliencePipeline<ModelResponse>? pipeline = null) : IModelClient
{
    // Built per instance: a breaker's failure history belongs to the one client it guards.
    private readonly ResiliencePipeline<ModelResponse> _pipeline = pipeline ?? CreateDefaultPipeline();

    private static ResiliencePipeline<ModelResponse> CreateDefaultPipeline() =>
        new ResiliencePipelineBuilder<ModelResponse>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ModelResponse>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                // Everything counts except the caller cancelling. Excluding every
                // OperationCanceledException, as Polly's own default does, also drops the client's
                // request timeout — HttpClient raises TaskCanceledException with the caller's token
                // untouched — and the breaker records what it does not handle as a success, so it
                // would never open on a provider that hangs.
                ShouldHandle = static args => ValueTask.FromResult(
                    args.Outcome.Exception is { } ex
                    && !(ex is OperationCanceledException && args.Context.CancellationToken.IsCancellationRequested))
            })
            .Build();

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        _pipeline.ExecuteAsync(
            static async (s, token) => await s.Client.CallAsync(s.Messages, s.Tools, token),
            (Client: inner, Messages: messages, Tools: availableTools),
            ct).AsTask();
}
