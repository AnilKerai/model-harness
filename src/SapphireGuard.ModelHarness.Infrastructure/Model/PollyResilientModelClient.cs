using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace SapphireGuard.ModelHarness.Infrastructure.Model;

/// <summary>
/// Decorates an <see cref="IModelClient"/> with retry + circuit-breaker via
/// Polly v8 resilience pipelines. Lives in Infrastructure so the Framework
/// stays free of Polly.
/// </summary>
public sealed class PollyResilientModelClient(
    IModelClient inner
) : IModelClient
{
    private readonly ResiliencePipeline<ModelResponse> _pipeline = new ResiliencePipelineBuilder<ModelResponse>()
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

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var state = (Client: inner, Messages: messages, Tools: availableTools);

        return await _pipeline.ExecuteAsync(
            static async (s, token) => await s.Client.CallAsync(s.Messages, s.Tools, token),
            state,
            ct);
    }
}
