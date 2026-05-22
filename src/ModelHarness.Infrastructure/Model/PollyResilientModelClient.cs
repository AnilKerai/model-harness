using ModelHarness.Framework.Model;
using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ModelHarness.Infrastructure.Model;

/// <summary>
/// Decorates an <see cref="IModelClient"/> with retry + circuit-breaker via
/// Polly v8 resilience pipelines. Lives in Infrastructure so the Framework
/// stays free of Polly.
/// </summary>
public sealed class PollyResilientModelClient : IModelClient
{
    private readonly IModelClient _inner;
    private readonly ResiliencePipeline<ModelResponse> _pipeline;

    public PollyResilientModelClient(IModelClient inner)
    {
        _inner = inner;
        _pipeline = new ResiliencePipelineBuilder<ModelResponse>()
            .AddRetry(new RetryStrategyOptions<ModelResponse>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<ModelResponse>().Handle<HttpRequestException>().Handle<TimeoutException>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ModelResponse>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder<ModelResponse>().Handle<HttpRequestException>().Handle<TimeoutException>()
            })
            .Build();
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        await _pipeline.ExecuteAsync(
            static async (state, token) => await state.Inner.CallAsync(state.Messages, state.Tools, token),
            (Inner: _inner, Messages: messages, Tools: availableTools),
            ct);
}
