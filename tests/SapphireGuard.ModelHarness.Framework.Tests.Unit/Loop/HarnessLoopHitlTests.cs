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
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

public sealed class HarnessLoopHitlTests
{
    private static HarnessLoop BuildHarness(IModelClient modelClient, IToolRegistry? toolRegistry = null, ICheckpointStore? checkpointStore = null, IContextBuilder? contextBuilder = null) =>
        new(
            modelClient: modelClient,
            toolRegistry: toolRegistry ?? new StubToolRegistry(),
            contextBuilder: contextBuilder ?? new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: checkpointStore ?? new NullCheckpointStore());

    // Records the turn index and run id of every checkpoint the loop saves, so a test can assert
    // that both survive a suspend/resume rather than restarting.
    private sealed class RecordingCheckpointStore : ICheckpointStore
    {
        public List<int> TurnNumbers { get; } = [];
        public List<string> RunIds { get; } = [];
        public AgentState? LastState { get; private set; }

        public Task SaveAsync(Checkpoint checkpoint, CancellationToken ct = default)
        {
            TurnNumbers.Add(checkpoint.TurnNumber);
            RunIds.Add(checkpoint.RunId);
            LastState = checkpoint.State;
            return Task.CompletedTask;
        }

        public Task<Checkpoint?> LoadAsync(string checkpointId, CancellationToken ct = default) => Task.FromResult<Checkpoint?>(null);
        public Task<Checkpoint?> LoadLatestAsync(string taskId, CancellationToken ct = default) => Task.FromResult<Checkpoint?>(null);
        public Task DeleteAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AgentState NewState() =>
        AgentState.NewTask("test task", new SapphireGuard.ModelHarness.Framework.State.Budget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static ModelResponse EndTurnResponse(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ModelResponse ToolUseResponse(params ToolCall[] calls) => new()
    {
        Text = null,
        ToolCalls = calls,
        StopReason = StopReason.ToolUse,
        Usage = Usage.Zero,
        Cost = 0m
    };

    // ── Suspension ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolReturnsPending_ReturnsAwaitingHuman()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your name?", IsPending: true), "ask_human");
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        Assert.Null(outcome.FinalAnswer);
        Assert.NotNull(outcome.PendingHumanInput);
        Assert.Equal(callId, outcome.PendingHumanInput.CallId);
        Assert.Equal("What is your name?", outcome.PendingHumanInput.Question);
    }

    [Fact]
    public async Task RunAsync_ToolReturnsPending_PendingStepInTrajectory()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "Approve this action?", IsPending: true), "ask_human");
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        var step = outcome.FinalState.Trajectory.OfType<ToolCallStep>().Single();
        Assert.True(step.Result.IsPending);
        Assert.Equal("Approve this action?", step.Result.Content);
    }

    [Fact]
    public async Task RunAsync_ToolReturnsPending_FinalStateCarriesPendingHumanInput()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your age?", IsPending: true), "ask_human");
        var client = new ScriptedModelClient(ToolUseResponse(toolCall));

        var harness = BuildHarness(client, registry);
        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.FinalState.Status);
        Assert.NotNull(outcome.FinalState.PendingHumanInput);
        Assert.Equal(callId, outcome.FinalState.PendingHumanInput.CallId);
        Assert.Equal("What is your age?", outcome.FinalState.PendingHumanInput.Question);
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ResumedAfterHumanAnswer_CompletesToDone()
    {
        var callId = Guid.NewGuid().ToString("n");
        var toolCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(callId, "What is your name?", IsPending: true), "ask_human");
        var firstClient = new ScriptedModelClient(ToolUseResponse(toolCall));

        var firstHarness = BuildHarness(firstClient, registry);
        var suspended = await firstHarness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, suspended.Status);

        var resumed = suspended.FinalState.ResumeWithHumanAnswer(callId, "Alice");
        var secondClient = new ScriptedModelClient(EndTurnResponse("Hello, Alice!"));
        var secondHarness = BuildHarness(secondClient);
        var final = await secondHarness.RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, final.Status);
        Assert.Equal("Hello, Alice!", final.FinalAnswer);
    }

    [Fact]
    public async Task RunAsync_ResumedAfterHumanAnswer_TurnNumberingContinues()
    {
        var askCallId = Guid.NewGuid().ToString("n");
        var askCall = new ToolCall(askCallId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var calcCall = new ToolCall(Guid.NewGuid().ToString("n"), "calc", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(call => call.ToolName == "ask_human"
            ? new ToolResult(askCallId, "What is your name?", IsPending: true)
            : new ToolResult(call.CallId, "42"), "ask_human", "calc");

        // Turn 0 runs a normal tool; turn 1 asks the human and suspends — so two model calls
        // (turns 0 and 1) are in the trajectory before resume.
        var firstClient = new ScriptedModelClient(ToolUseResponse(calcCall), ToolUseResponse(askCall));
        var firstStore = new RecordingCheckpointStore();
        var firstHarness = BuildHarness(firstClient, registry, firstStore);
        var suspended = await firstHarness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, suspended.Status);
        Assert.Equal([0, 1, 1], firstStore.TurnNumbers); // top-of-turn 0, top-of-turn 1, suspension

        var resumed = suspended.FinalState.ResumeWithHumanAnswer(askCallId, "Alice");
        var secondClient = new ScriptedModelClient(EndTurnResponse("Hello, Alice!"));
        var secondStore = new RecordingCheckpointStore();
        var secondHarness = BuildHarness(secondClient, toolRegistry: null, checkpointStore: secondStore);
        var final = await secondHarness.RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, final.Status);
        // The resumed run must continue at turn 2, not restart at 0.
        Assert.Equal(2, secondStore.TurnNumbers[0]);
    }

    [Fact]
    public async Task RunAsync_ResumedAfterHumanAnswer_RunIdIsStableAcrossTheResume()
    {
        // Checkpoint.RunId documents "all checkpoints for the same task share this value", but the
        // loop used to mint a fresh GUID per RunAsync — so a suspend/resume split one logical run
        // across two RunIds and broke correlation. It is now derived from the trajectory.
        var askCallId = Guid.NewGuid().ToString("n");
        var askCall = new ToolCall(askCallId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var registry = new StubToolRegistry(_ => new ToolResult(askCallId, "What is your name?", IsPending: true), "ask_human");

        var firstStore = new RecordingCheckpointStore();
        var firstHarness = BuildHarness(new ScriptedModelClient(ToolUseResponse(askCall)), registry, firstStore);
        var suspended = await firstHarness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, suspended.Status);

        var resumed = suspended.FinalState.ResumeWithHumanAnswer(askCallId, "Alice");
        var secondStore = new RecordingCheckpointStore();
        var secondHarness = BuildHarness(
            new ScriptedModelClient(EndTurnResponse("Hello, Alice!")), toolRegistry: null, checkpointStore: secondStore);
        var final = await secondHarness.RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, final.Status);
        Assert.NotEmpty(firstStore.RunIds);
        Assert.NotEmpty(secondStore.RunIds);
        // Every checkpoint on both sides of the suspend carries one and the same run id.
        Assert.Single(firstStore.RunIds.Concat(secondStore.RunIds).Distinct());
    }

    // ── Several calls in one response ────────────────────────────────────────
    // A response can carry calls after the one that suspends the run. They are not run — the human may be
    // about to be asked whether one of them is allowed — but they must not vanish either.

    private const string NotExecutedText =
        "Not executed: the run suspended for human input before this call ran. Re-issue it after reading the answer if it is still needed.";

    private static ToolCall CallTo(string tool) => new(Guid.NewGuid().ToString("n"), tool, JsonDocument.Parse("{}").RootElement);

    // The real pipeline, so a test can read the prompt the model is sent after a resume.
    private static DefaultContextBuilder RealContextBuilder() =>
        new(new DefaultGuideRunner(
            [new ToolSelectorGuide(new PassthroughToolSelector())],
            new HeadEvictionTrajectoryGuide(new NullCompactionStrategy(), new CompactionOptions { WindowTokens = 100_000 })));

    [Fact]
    public async Task RunAsync_CallsFollowAskHuman_SuspendsWithoutRunningThemAndRecordsEachAsNotExecuted()
    {
        var ask = new PendingTool("ask_human", "May I delete X?");
        var toolB = new CountingTool("delete");
        var toolC = new CountingTool("archive");
        var askCall = CallTo("ask_human");
        var callB = CallTo("delete");
        var callC = CallTo("archive");
        var client = new ScriptedModelClient(ToolUseResponse(askCall, callB, callC));
        var store = new RecordingCheckpointStore();
        var harness = BuildHarness(client, new InMemoryToolRegistry([ask, toolB, toolC]), store);

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        Assert.Equal(0, toolB.ExecuteCount);
        Assert.Equal(0, toolC.ExecuteCount);
        Assert.Equal(askCall.CallId, outcome.PendingHumanInput!.CallId);
        Assert.Equal("May I delete X?", outcome.PendingHumanInput.Question);

        var steps = outcome.FinalState.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(["ask_human", "delete", "archive"], steps.Select(s => s.Call.ToolName));   // the pending step first, then call order
        Assert.True(steps[0].Result.IsPending);
        Assert.All(steps.Skip(1), s =>
        {
            Assert.True(s.Result.IsError);
            Assert.False(s.Result.IsPending);
            Assert.Equal(NotExecutedText, s.Result.Content);
            Assert.Equal(s.Call.CallId, s.Result.CallId);   // the provider gets a tool_result for every tool_use
        });

        // The checkpoint a restarted process would load carries the same steps.
        Assert.Equal(AgentStatus.AwaitingHuman, store.LastState!.Status);
        Assert.Equal(outcome.FinalState.Trajectory.Count, store.LastState.Trajectory.Count);
        Assert.Equal(askCall.CallId, store.LastState.PendingHumanInput!.CallId);
    }

    [Fact]
    public async Task RunAsync_CallsFollowAskHuman_RunsNoSensorsForThem()
    {
        var sensor = new CountingSensor(HookPoint.PreToolCall, HookPoint.PostToolCall);
        var harness = new HarnessLoop(
            modelClient: new ScriptedModelClient(ToolUseResponse(CallTo("ask_human"), CallTo("delete"))),
            toolRegistry: new InMemoryToolRegistry([new PendingTool("ask_human", "May I delete X?"), new CountingTool("delete")]),
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([sensor]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore());

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        Assert.Equal(2, sensor.CheckCount);   // PreToolCall and PostToolCall for ask_human only
    }

    [Fact]
    public async Task RunAsync_ResumedAfterCallsWereSkipped_PromptCarriesTheNotExecutedPair()
    {
        var toolB = new CountingTool("delete");
        var registry = new InMemoryToolRegistry([new PendingTool("ask_human", "May I delete X?"), toolB]);
        var askCall = CallTo("ask_human");
        var callB = CallTo("delete");
        var suspended = await BuildHarness(
                new ScriptedModelClient(ToolUseResponse(askCall, callB)), registry, contextBuilder: RealContextBuilder())
            .RunAsync(NewState(), CancellationToken.None);
        Assert.Equal(AgentStatus.AwaitingHuman, suspended.Status);

        var resumed = suspended.FinalState.ResumeWithHumanAnswer(askCall.CallId, "No, leave X alone");
        var client = new PromptRecordingModelClient(EndTurnResponse("Understood."));
        var final = await BuildHarness(client, registry, contextBuilder: RealContextBuilder())
            .RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, final.Status);
        Assert.Equal("Understood.", final.FinalAnswer);
        Assert.Equal(0, toolB.ExecuteCount);

        var prompt = Assert.Single(client.Prompts);
        Assert.Contains(prompt, m => m.Role == MessageRole.ToolUse && m.Content.StartsWith($"[tool_call name=delete id={callB.CallId}]"));
        Assert.Contains(prompt, m => m.Role == MessageRole.Tool && m.Content == $"[tool_result id={callB.CallId} error=True] {NotExecutedText}");
        // The answered ask_human pair is there too, ahead of the skipped one.
        var toolMessages = prompt.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content).ToList();
        Assert.Equal(
            [$"[tool_result id={askCall.CallId} error=False] No, leave X alone", $"[tool_result id={callB.CallId} error=True] {NotExecutedText}"],
            toolMessages);
    }

    [Fact]
    public async Task RunAsync_CallsPrecedeAskHuman_RunsThemThenSuspends()
    {
        var toolA = new CountingTool("calc");
        var callA = CallTo("calc");
        var askCall = CallTo("ask_human");
        var client = new ScriptedModelClient(ToolUseResponse(callA, askCall));
        var harness = BuildHarness(client, new InMemoryToolRegistry([toolA, new PendingTool("ask_human", "Ready?")]));

        var outcome = await harness.RunAsync(NewState(), CancellationToken.None);

        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        Assert.Equal(1, toolA.ExecuteCount);
        var steps = outcome.FinalState.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(["calc", "ask_human"], steps.Select(s => s.Call.ToolName));
        Assert.False(steps[0].Result.IsError);
        Assert.Equal("ran", steps[0].Result.Content);
        Assert.True(steps[1].Result.IsPending);
        Assert.Equal(askCall.CallId, outcome.PendingHumanInput!.CallId);
    }

    private sealed class PendingTool(string name, string question) : ITool
    {
        public string Name => name;
        public string Description => "asks a human";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolResult(call.CallId, question, IsPending: true));
    }

    private sealed class CountingTool(string name) : ITool
    {
        private int _executeCount;
        public int ExecuteCount => _executeCount;

        public string Name => name;
        public string Description => "counts executions";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executeCount);
            return Task.FromResult(new ToolResult(call.CallId, "ran"));
        }
    }

    private sealed class CountingSensor(params HookPoint[] hookPoints) : ISensor
    {
        private int _checkCount;
        public int CheckCount => _checkCount;

        public string Name => "counting";
        public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint>(hookPoints);

        public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        {
            Interlocked.Increment(ref _checkCount);
            return Task.FromResult(SensorResult.Pass);
        }
    }

    private sealed class PromptRecordingModelClient(params ModelResponse[] responses) : IModelClient
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public List<IReadOnlyList<Message>> Prompts { get; } = [];

        public Task<ModelResponse> CallAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
        {
            Prompts.Add(messages);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    // ── AgentState.ResumeWithHumanAnswer ─────────────────────────────────────

    [Fact]
    public void ResumeWithHumanAnswer_ClearsPendingHumanInput()
    {
        var callId = Guid.NewGuid().ToString("n");
        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "Ready?", IsPending: true));

        var suspended = NewState()
            .AppendStep(pendingStep) with
            {
                Status = AgentStatus.AwaitingHuman,
                PendingHumanInput = new PendingHumanInput(callId, "Ready?")
            };

        var resumed = suspended.ResumeWithHumanAnswer(callId, "yes");

        Assert.Equal(AgentStatus.Running, resumed.Status);
        Assert.Null(resumed.PendingHumanInput);
    }

    [Fact]
    public void ResumeWithHumanAnswer_ReplacesPendingStep()
    {
        var callId = Guid.NewGuid().ToString("n");
        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "What is your name?", IsPending: true));

        var suspended = NewState()
            .AppendStep(pendingStep) with { Status = AgentStatus.AwaitingHuman };

        var resumed = suspended.ResumeWithHumanAnswer(callId, "Bob");

        Assert.Equal(AgentStatus.Running, resumed.Status);
        var resolvedStep = resumed.Trajectory.OfType<ToolCallStep>().Single();
        Assert.False(resolvedStep.Result.IsPending);
        Assert.Equal("Bob", resolvedStep.Result.Content);
    }

    [Fact]
    public void ResumeWithHumanAnswer_LeavesOtherStepsUntouched()
    {
        var callId = Guid.NewGuid().ToString("n");
        var otherCallId = Guid.NewGuid().ToString("n");

        var otherCall = new ToolCall(otherCallId, "calc", JsonDocument.Parse("{}").RootElement);
        var otherStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, otherCall,
            new ToolResult(otherCallId, "42"));

        var pendingCall = new ToolCall(callId, "ask_human", JsonDocument.Parse("{}").RootElement);
        var pendingStep = new ToolCallStep(
            Guid.NewGuid(), DateTimeOffset.UtcNow, pendingCall,
            new ToolResult(callId, "Ready?", IsPending: true));

        var state = NewState().AppendStep(otherStep).AppendStep(pendingStep);
        var resumed = state.ResumeWithHumanAnswer(callId, "yes");

        var steps = resumed.Trajectory.OfType<ToolCallStep>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("42", steps[0].Result.Content);
        Assert.False(steps[0].Result.IsPending);
        Assert.Equal("yes", steps[1].Result.Content);
        Assert.False(steps[1].Result.IsPending);
    }
}
