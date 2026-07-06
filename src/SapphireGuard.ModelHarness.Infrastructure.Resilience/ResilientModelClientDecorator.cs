using System.Diagnostics.CodeAnalysis;
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
/// this decorator adds only the circuit breaker the SDKs lack. The default breaker trips on any non-cancellation
/// failure surfacing from the inner client (provider SDKs raise their own exception types, so a
/// transport-only predicate would never fire); supply a custom <see cref="ResiliencePipeline{T}"/> via
/// the constructor to narrow it.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class ResilientModelClientDecorator(
    IModelClient inner,
    ResiliencePipeline<ModelResponse>? pipeline = null) : IModelClient
{
    private static readonly ResiliencePipeline<ModelResponse> DefaultPipeline =
        new ResiliencePipelineBuilder<ModelResponse>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ModelResponse>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder<ModelResponse>().Handle<Exception>(ex => ex is not OperationCanceledException)
            })
            .Build();

    private readonly ResiliencePipeline<ModelResponse> _pipeline = pipeline ?? DefaultPipeline;

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        _pipeline.ExecuteAsync(
            static async (s, token) => await s.Client.CallAsync(s.Messages, s.Tools, token),
            (Client: inner, Messages: messages, Tools: availableTools),
            ct).AsTask();
}
