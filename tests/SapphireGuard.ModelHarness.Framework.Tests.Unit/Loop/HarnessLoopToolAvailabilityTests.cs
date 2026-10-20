using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Security;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

// The loop dispatches a call only if its tool was among the definitions sent with that model call. The
// selector and tool suppression decide what the model is shown; these tests pin that they also bind.
public sealed class HarnessLoopToolAvailabilityTests
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    private static ModelResponse ToolUse(params ToolCall[] calls) => new()
    {
        Text = null,
        ToolCalls = calls,
        StopReason = StopReason.ToolUse,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ModelResponse EndTurn(string text = "done") => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ToolCall CallTo(string tool) => new(Guid.NewGuid().ToString("n"), tool, EmptyObject);

    private static AgentState NewState() =>
        AgentState.NewTask("test task", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    // The real pipeline — registry, ToolSelectorGuide, DefaultContextBuilder — so the test exercises the
    // path a consumer's IToolSelector takes, not a stand-in for it.
    private static HarnessLoop BuildHarness(
        IModelClient client,
        IReadOnlyList<ITool> registered,
        IToolSelector selector,
        IEnumerable<ISensor>? sensors = null) =>
        new(
            modelClient: client,
            toolRegistry: new InMemoryToolRegistry(registered),
            contextBuilder: new DefaultContextBuilder(new DefaultGuideRunner(
                [new ToolSelectorGuide(selector)],
                new HeadEvictionTrajectoryGuide(new NullCompactionStrategy(), new CompactionOptions { WindowTokens = 100_000 }))),
            sensorRunner: new DefaultSensorRunner(sensors ?? []),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

    [Fact]
    public async Task RunAsync_SelectorExcludesTool_ModelCallsIt_ToolIsNotExecutedAndRunContinues()
    {
        var allowed = new CountingTool("allowed");
        var withheld = new CountingTool("withheld");
        var preToolCalls = new CountingSensor(HookPoint.PreToolCall);
        var client = new OfferRecordingModelClient(ToolUse(CallTo("withheld")), EndTurn("replanned"));
        var harness = BuildHarness(client, [allowed, withheld], new OnlyTheseTools("allowed"), [preToolCalls]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("replanned", outcome.FinalAnswer);
        Assert.Equal(2, client.CallCount);                       // the run went on to its next model call
        Assert.Equal(["allowed"], client.Offered[0]);            // the model was never shown the tool
        Assert.Equal(0, withheld.ExecuteCount);
        Assert.Equal(0, preToolCalls.CheckCount);                // no sensor ran for a call that was not dispatched

        var step = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.Equal("withheld", step.Call.ToolName);
        Assert.True(step.Result.IsError);
        Assert.Equal("Tool 'withheld' is not available on this turn.", step.Result.Content);
        Assert.Equal(step.Call.CallId, step.Result.CallId);      // the provider gets a tool_result for the tool_use
    }

    [Fact]
    public async Task RunAsync_ToolSuppressedAfterPreReturnChallenge_ModelCallsATool_ToolIsNotExecuted()
    {
        var tool = new CountingTool("allowed");
        var client = new OfferRecordingModelClient(
            EndTurn("first attempt"),           // PreReturn challenges this and suppresses tools
            ToolUse(CallTo("allowed")),         // sent with no tools; the model names one anyway
            EndTurn("final"));
        var harness = BuildHarness(client, [tool], new PassthroughToolSelector(), [new SuppressOnceAtPreReturn()]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("final", outcome.FinalAnswer);
        Assert.Equal(3, client.CallCount);
        Assert.Equal(["allowed"], client.Offered[0]);
        Assert.Empty(client.Offered[1]);                         // the suppressed turn offered nothing
        Assert.Equal(["allowed"], client.Offered[2]);            // suppression lasts one call
        Assert.Equal(0, tool.ExecuteCount);

        var step = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.True(step.Result.IsError);
        Assert.Equal("Tool 'allowed' is not available on this turn.", step.Result.Content);
    }

    [Fact]
    public async Task RunAsync_ModelCallsAnOfferedTool_ToolIsStillExecuted()
    {
        var tool = new CountingTool("allowed");
        var client = new OfferRecordingModelClient(ToolUse(CallTo("allowed")), EndTurn("done"));
        var harness = BuildHarness(client, [tool], new PassthroughToolSelector());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(1, tool.ExecuteCount);
        var step = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.False(step.Result.IsError);
        Assert.Equal("ran", step.Result.Content);
    }

    [Fact]
    public async Task RunAsync_OneResponseNamesAnOfferedAndAWithheldTool_OnlyTheOfferedOneRuns()
    {
        var allowed = new CountingTool("allowed");
        var withheld = new CountingTool("withheld");
        var client = new OfferRecordingModelClient(ToolUse(CallTo("withheld"), CallTo("allowed")), EndTurn("done"));
        var harness = BuildHarness(client, [allowed, withheld], new OnlyTheseTools("allowed"));

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(0, withheld.ExecuteCount);
        Assert.Equal(1, allowed.ExecuteCount);
        var steps = outcome.FinalState.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(["withheld", "allowed"], steps.Select(s => s.Call.ToolName));   // one result per call, in call order
        Assert.True(steps[0].Result.IsError);
        Assert.False(steps[1].Result.IsError);
    }

    [Fact]
    public async Task RunAsync_ModelNamesAWithheldUntrustedTool_DoesNotTaintTheRun()
    {
        // The harness wrote "not available" itself and dispatched nothing, so no content from the
        // untrusted source entered the trajectory and a later privileged action must still run.
        var fetch = new CountingTool("fetch_webpage");
        var email = new CountingTool("send_email");
        var taint = new TaintTrackingSensor(new TrustPolicy(
            untrustedSources: ["fetch_webpage"], privilegedActions: ["send_email"]));
        var client = new OfferRecordingModelClient(
            ToolUse(CallTo("fetch_webpage")), ToolUse(CallTo("send_email")), EndTurn("sent"));
        var harness = BuildHarness(client, [fetch, email], new OnlyTheseTools("send_email"), [taint]);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(0, fetch.ExecuteCount);
        Assert.Equal(1, email.ExecuteCount);

        var steps = outcome.FinalState.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(["fetch_webpage", "send_email"], steps.Select(s => s.Call.ToolName));
        Assert.True(steps[0].Result.IsError);
        Assert.True(steps[0].HarnessAuthored);
        Assert.False(steps[1].Result.IsError);
        Assert.DoesNotContain(outcome.FinalState.Trajectory.OfType<SensorInterventionStep>(),
            s => s.SensorName == "taint-tracking");
    }

    private sealed class OnlyTheseTools(params string[] names) : IToolSelector
    {
        public Task<IReadOnlyList<ITool>> SelectAsync(IReadOnlyList<ITool> tools, AgentState state, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ITool>>([.. tools.Where(t => names.Contains(t.Name))]);
    }

    private sealed class CountingTool(string name) : ITool
    {
        private int _executeCount;
        public int ExecuteCount => _executeCount;

        public string Name => name;
        public string Description => "counts executions";
        public JsonElement InputSchema => EmptyObject;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executeCount);
            return Task.FromResult(new ToolResult(call.CallId, "ran"));
        }
    }

    // Records the tool names sent with each call, so a test can say what the model was and was not shown.
    private sealed class OfferRecordingModelClient(params ModelResponse[] responses) : IModelClient
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public List<string[]> Offered { get; } = [];
        public int CallCount => Offered.Count;

        public Task<ModelResponse> CallAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
        {
            Offered.Add([.. availableTools.Select(t => t.Name)]);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class CountingSensor(HookPoint hookPoint) : ISensor
    {
        private int _checkCount;
        public int CheckCount => _checkCount;

        public string Name => "counting";
        public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { hookPoint };

        public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkCount);
            return Task.FromResult(SensorResult.Pass);
        }
    }

    private sealed class SuppressOnceAtPreReturn : ISensor
    {
        private bool _fired;

        public string Name => "suppress-once";
        public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreReturn };

        public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        {
            if (_fired)
                return Task.FromResult(SensorResult.Pass);
            _fired = true;
            return Task.FromResult(SensorResult.InterveneWithToolSuppression("reformat without tools"));
        }
    }
}
