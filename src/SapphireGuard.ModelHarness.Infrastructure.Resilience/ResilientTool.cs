using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

[ExcludeFromCodeCoverage]
public sealed class ResilientTool(ITool inner, ResiliencePipeline<ToolResult>? pipeline = null) : ITool
{
    private static readonly ResiliencePipeline<ToolResult> DefaultPipeline =
        new ResiliencePipelineBuilder<ToolResult>()
            .AddRetry(new RetryStrategyOptions<ToolResult>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = new PredicateBuilder<ToolResult>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ToolResult>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder<ToolResult>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
            })
            .Build();

    private readonly ResiliencePipeline<ToolResult> _pipeline = pipeline ?? DefaultPipeline;

    public string Name => inner.Name;
    public string Description => inner.Description;
    public JsonElement InputSchema => inner.InputSchema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        _pipeline.ExecuteAsync(
            static async (s, token) => await s.Inner.ExecuteAsync(s.Call, s.Context, token),
            (Inner: inner, Call: call, Context: context),
            ct).AsTask();
}
