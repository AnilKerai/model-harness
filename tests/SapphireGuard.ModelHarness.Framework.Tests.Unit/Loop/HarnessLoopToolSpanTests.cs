using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

// The execute_tool span is the tool's execution, nothing else. PostToolCall sensors can be AI sensors
// with model calls of their own, so a span that outlives the tool would absorb their time and nest
// their spans under the tool.
public sealed class HarnessLoopToolSpanTests
{
    private static AgentState NewState() =>
        AgentState.NewTask("test task", new StateBudget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static ModelResponse ToolUseResponse(string tool) => new()
    {
        Text = null,
        ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), tool, JsonDocument.Parse("{}").RootElement)],
        StopReason = StopReason.ToolUse,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ModelResponse EndTurnResponse() => new()
    {
        Text = "done",
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static HarnessLoop BuildHarness(ISensor sensor) =>
        new(
            modelClient: new ScriptedModelClient(ToolUseResponse("calc"), EndTurnResponse()),
            toolRegistry: new StubToolRegistry("calc"),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([sensor]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new OpenTelemetryTracer(),
            checkpointStore: new NullCheckpointStore());

    [Fact]
    public async Task ExecuteToolSpan_HasStoppedBeforePostToolCallSensorsRun()
    {
        var state = NewState();
        var started = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OpenTelemetryTracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add, // other test classes emit on this source in parallel; the task id filters them
        };
        ActivitySource.AddActivityListener(listener);
        var sensor = new SpanProbeSensor(() => started.Where(a =>
            a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal)
            && (string?)a.GetTagItem("gen_ai.conversation.id") == state.TaskId).ToList());

        var outcome = await BuildHarness(sensor).RunAsync(state, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        var probe = Assert.Single(sensor.Probes);
        Assert.Equal(1, probe.ToolSpans);
        Assert.True(probe.ToolSpanStopped, "the execute_tool span was still open while the PostToolCall sensor ran");
        // With the tool span closed, a span the sensor opens is a sibling of it, under the run.
        Assert.Equal("invoke_agent", probe.CurrentOperation);
    }

    private sealed record Probe(int ToolSpans, bool ToolSpanStopped, string? CurrentOperation);

    private sealed class SpanProbeSensor(Func<IReadOnlyList<Activity>> toolSpans) : ISensor
    {
        public ConcurrentQueue<Probe> Probes { get; } = new();

        public string Name => "span-probe";
        public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PostToolCall };

        public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        {
            var spans = toolSpans();
            Probes.Enqueue(new Probe(spans.Count, spans.All(a => a.IsStopped), Activity.Current?.OperationName));
            return Task.FromResult(SensorResult.Pass);
        }
    }
}
