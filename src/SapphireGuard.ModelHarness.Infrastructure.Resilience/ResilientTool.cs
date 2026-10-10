using System.Text.Json;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

/// <summary>
/// Wraps an <see cref="ITool"/> with a Polly retry and circuit breaker, for tools that call an
/// external service and have no resilience of their own.
/// </summary>
/// <remarks>
/// The default policy retries <see cref="HttpRequestException"/> and <see cref="TimeoutException"/>
/// up to three times with exponential back-off, and its breaker counts those plus the tool's own
/// timeout — an <see cref="OperationCanceledException"/> raised with the caller's token untouched, as
/// <see cref="HttpClient"/> does. That timeout is counted but not retried: the call may already have
/// run its side effect. The caller cancelling (which includes the harness's per-tool deadline) is
/// neither. Each tool built without a <see cref="ResiliencePipeline{T}"/> gets its own breaker, so one
/// failing backend never fast-fails another tool; pass the same pipeline to several to share one.
/// </remarks>
public sealed class ResilientTool(ITool inner, ResiliencePipeline<ToolResult>? pipeline = null) : ITool
{
    // Built per instance: a breaker's failure history belongs to the one tool it guards.
    private readonly ResiliencePipeline<ToolResult> _pipeline = pipeline ?? CreateDefaultPipeline();

    private static ResiliencePipeline<ToolResult> CreateDefaultPipeline() =>
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
                ShouldHandle = static args => ValueTask.FromResult(args.Outcome.Exception switch
                {
                    HttpRequestException or TimeoutException => true,
                    // The tool's own timeout: HttpClient raises TaskCanceledException with the
                    // caller's token untouched. Left out, the breaker records it as a success.
                    OperationCanceledException => !args.Context.CancellationToken.IsCancellationRequested,
                    _ => false
                })
            })
            .Build();

    public string Name => inner.Name;
    public string Description => inner.Description;
    public JsonElement InputSchema => inner.InputSchema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        _pipeline.ExecuteAsync(
            static async (s, token) => await s.Inner.ExecuteAsync(s.Call, s.Context, token),
            (Inner: inner, Call: call, Context: context),
            ct).AsTask();
}
