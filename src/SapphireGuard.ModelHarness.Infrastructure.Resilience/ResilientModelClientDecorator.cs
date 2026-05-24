using System.Diagnostics.CodeAnalysis;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

[ExcludeFromCodeCoverage]
public sealed class ResilientModelClientDecorator(
    IModelClient inner,
    ResiliencePipeline<ModelResponse>? pipeline = null) : IModelClient
{
    private static readonly ResiliencePipeline<ModelResponse> DefaultPipeline =
        new ResiliencePipelineBuilder<ModelResponse>()
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
