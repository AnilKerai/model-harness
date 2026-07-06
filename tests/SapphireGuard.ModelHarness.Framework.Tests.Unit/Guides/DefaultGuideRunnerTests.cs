using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class DefaultGuideRunnerTests
{
    private static readonly StateBudget Budget = new()
    {
        MaxTurns = 10,
        MaxTotalTokens = 1000,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };

    private static AgentState State() =>
        AgentState.NewTask("t", Budget, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class RecordingGuide(string name, List<string> log) : IGuide
    {
        public string Name => name;
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTrajectoryGuide(List<string> log) : ITrajectoryGuide
    {
        public string Name => "trajectory";
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            log.Add(Name);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunsSupportingGuidesInRegistrationOrder_ThenTrajectoryGuideLast()
    {
        var log = new List<string>();
        var runner = new DefaultGuideRunner(
            [new RecordingGuide("a", log), new RecordingGuide("b", log), new RecordingGuide("c", log)],
            new RecordingTrajectoryGuide(log));

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c", "trajectory" }, log);
    }

    [Fact]
    public async Task TrajectoryGuideRunsLast_EvenWithNoSupportingGuides()
    {
        var log = new List<string>();
        var runner = new DefaultGuideRunner([], new RecordingTrajectoryGuide(log));

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "trajectory" }, log);
    }

    [Fact]
    public async Task EmitsOneGuideContribution_PerSupportingGuideAndTrajectory()
    {
        var tracer = new RecordingTracer();
        var runner = new DefaultGuideRunner(
            [new RecordingGuide("a", []), new RecordingGuide("b", [])],
            new RecordingTrajectoryGuide([]),
            tracer);

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "trajectory" }, tracer.Contributions.Select(c => c.Guide));
    }

    [Fact]
    public async Task GuideContribution_CapturesToolRemovalAndSnippetDelta()
    {
        var tracer = new RecordingTracer();
        var runner = new DefaultGuideRunner(
            [new DropCalcAndRecallGuide()],
            new RecordingTrajectoryGuide([]),
            tracer);

        await runner.RunAsync(
            State(),
            [new StubTool("calc", "maths"), new StubTool("search", "web")],
            CancellationToken.None);

        var contribution = tracer.Contributions.Single(c => c.Guide == "drop-calc").Contribution;
        Assert.Equal(2, contribution.ToolsBefore);
        Assert.Equal(1, contribution.ToolsAfter);
        Assert.Equal(["calc"], contribution.ToolsRemoved);
        Assert.Empty(contribution.ToolsAdded);
        Assert.Equal(1, contribution.MemorySnippetsAdded);
    }

    [Fact]
    public async Task GuideContributions_TaggedWithTurnIndex_FromPriorModelCallCount()
    {
        var tracer = new RecordingTracer();
        var runner = new DefaultGuideRunner(
            [new RecordingGuide("a", [])],
            new RecordingTrajectoryGuide([]),
            tracer);

        // Two prior model calls already in the trajectory → the pipeline is shaping turn index 2.
        var state = State().AppendStep(ModelStep()).AppendStep(ModelStep());

        await runner.RunAsync(state, [], CancellationToken.None);

        Assert.NotEmpty(tracer.Contributions);
        Assert.All(tracer.Contributions, c => Assert.Equal(2, c.Turn));
    }

    private static ModelCallStep ModelStep() => new(
        Guid.NewGuid(),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Prompt: [],
        Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m },
        Usage: Usage.Zero,
        Cost: 0m);

    private sealed class DropCalcAndRecallGuide : IGuide
    {
        public string Name => "drop-calc";
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            draft.AvailableTools = draft.AvailableTools.Where(t => t.Name != "calc").ToList();
            draft.MemorySnippets.Add("recalled fact");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTracer : ITracer
    {
        public List<(string Guide, int Turn, GuideContribution Contribution)> Contributions { get; } = [];
        public void StartTrace(string taskId, string taskText) { }
        public IModelCallScope BeginModelCall(string taskId, int turn, IReadOnlyList<Message> prompt, IReadOnlyList<ToolDefinition> tools) => NoopScope.Instance;
        public IToolCallScope BeginToolCall(string taskId, int turn, ToolCall call) => NoopScope.Instance;
        public void LogSensorResult(string taskId, int turn, HookPoint hookPoint, string sensorName, SensorResult result) { }
        public void Complete(string taskId, AgentStatus status, string? failureReason) { }
        public void LogGuideContribution(string taskId, int turn, string guideName, GuideContribution contribution)
            => Contributions.Add((guideName, turn, contribution));
        public List<CompactionTrace> Compactions { get; } = [];
        public void LogCompaction(string taskId, int turn, CompactionTrace trace) => Compactions.Add(trace);
        public List<(string Guide, string Error)> GuideErrors { get; } = [];
        public void LogGuideError(string taskId, int turn, string guideName, string error) => GuideErrors.Add((guideName, error));
    }

    private sealed class ThrowingGuide(string name) : IGuide
    {
        public string Name => name;
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
            => throw new InvalidOperationException("guide exploded");
    }

    [Fact]
    public async Task SupportingGuideThrows_IsSkippedAndLoggedAsError_OtherGuidesAndTrajectoryStillRun()
    {
        // A supporting guide's contribution is an optional enhancement; if it throws, the pipeline
        // must skip it (recording an error) and still run the remaining guides + the trajectory guide.
        var tracer = new RecordingTracer();
        var log = new List<string>();
        var runner = new DefaultGuideRunner(
            [new RecordingGuide("a", log), new ThrowingGuide("boom"), new RecordingGuide("c", log)],
            new RecordingTrajectoryGuide(log),
            tracer);

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "a", "c", "trajectory" }, log);

        var error = Assert.Single(tracer.GuideErrors);
        Assert.Equal("boom", error.Guide);
        Assert.Contains("InvalidOperationException", error.Error);
        Assert.DoesNotContain(tracer.Contributions, c => c.Guide == "boom");
    }

    [Fact]
    public async Task EmitsCompactionTrace_WhenTrajectoryGuidePopulatesOne()
    {
        var tracer = new RecordingTracer();
        var runner = new DefaultGuideRunner([], new CompactingTrajectoryGuide(), tracer);

        await runner.RunAsync(State(), [], CancellationToken.None);

        var trace = Assert.Single(tracer.Compactions);
        Assert.Equal(3, trace.StepsEvicted);
        Assert.True(trace.Folded);
    }

    private sealed class CompactingTrajectoryGuide : ITrajectoryGuide
    {
        public string Name => "trajectory";
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            draft.CompactionTrace = new CompactionTrace(StepsEvicted: 3, TokensReclaimed: 120, Folded: true, Usage.Zero, Cost: 0.001m);
            return Task.CompletedTask;
        }
    }
}

file sealed class StubTool(string name, string description) : ITool
{
    public string Name => name;
    public string Description => description;
    public JsonElement InputSchema => JsonDocument.Parse("{}").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, "ok"));
}
