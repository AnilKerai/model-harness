using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using BudgetNs = SapphireGuard.ModelHarness.Framework.Budget;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Loop;

/// <summary>
/// Pins a deliberate behaviour so a later change to it is itself deliberate: <c>Budget.MaxWallClock</c>
/// includes the time a HITL run spends suspended waiting for the human. A resume adds no user message,
/// so both enforcers keep measuring from the same anchor and a late answer finds the budget already
/// spent. The documented remedy is to extend <c>Budget.MaxWallClock</c> on the resumed state.
/// </summary>
public sealed class HarnessLoopHitlWallClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HumanLatency = TimeSpan.FromMinutes(5);

    private const string AskCallId = "ask-1";

    public static TheoryData<string> EnforcerKinds => new() { "default", "turn-scoped" };

    // ── The failure mode ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EnforcerKinds))]
    public async Task Resume_HumanAnswersAfterMaxWallClock_FinalisesImmediatelyWithoutTools(string enforcerKind)
    {
        var clock = new MutableClock(Start);
        var client = new ToolCountingModelClient(
            ToolUse(AskCall()),          // suspends at ask_human
            EndTurn("best effort"));     // the forced finalisation call after the resume
        var suspended = await SuspendAsync(client, clock, enforcerKind);

        clock.Advance(HumanLatency); // the human takes far longer than the 1-minute cap
        var resumed = suspended.FinalState.ResumeWithHumanAnswer(AskCallId, "Alice");
        var outcome = await BuildHarness(client, new ListingToolRegistry(), clock, enforcerKind)
            .RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.PartialResult, outcome.Status);
        Assert.Contains("Reached MaxWallClock", outcome.FailureReason);
        Assert.Equal("best effort", outcome.FinalAnswer);
        // Call 1 was offered the tool (so the registry really does list one); call 2 — the only
        // post-resume call — is the tool-less finalisation.
        Assert.Equal([1, 0], client.ToolsOffered);
    }

    [Theory]
    [MemberData(nameof(EnforcerKinds))]
    public async Task Resume_HumanAnswersWithinMaxWallClock_Continues(string enforcerKind)
    {
        // Control for the test above: with the same wiring and a prompt answer the run carries on, so
        // it is the elapsed wait — nothing else about the resume — that exhausts the budget.
        var clock = new MutableClock(Start);
        var client = new ToolCountingModelClient(ToolUse(AskCall()), EndTurn("done"));
        var suspended = await SuspendAsync(client, clock, enforcerKind);

        clock.Advance(TimeSpan.FromSeconds(10));
        var resumed = suspended.FinalState.ResumeWithHumanAnswer(AskCallId, "Alice");
        var outcome = await BuildHarness(client, new ListingToolRegistry(), clock, enforcerKind)
            .RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal([1, 1], client.ToolsOffered);
    }

    // ── The documented remedy ─────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EnforcerKinds))]
    public async Task Resume_BudgetExtendedOnResumedState_ContinuesWithToolsAndFinishesDone(string enforcerKind)
    {
        var clock = new MutableClock(Start);
        var client = new ToolCountingModelClient(
            ToolUse(AskCall()),                                      // suspends at ask_human
            ToolUse(new ToolCall("lookup-1", "lookup", EmptyArgs())), // post-resume: still has tools
            EndTurn("all done"));
        var suspended = await SuspendAsync(client, clock, enforcerKind);

        clock.Advance(HumanLatency);
        var state = suspended.FinalState;
        // The remedy from docs/EXTENDING.md: Budget is `init`, so the host extends it on resume.
        var resumed = state.ResumeWithHumanAnswer(AskCallId, "Alice")
            with { Budget = state.Budget with { MaxWallClock = TimeSpan.FromMinutes(30) } };
        var registry = new ListingToolRegistry();
        var outcome = await BuildHarness(client, registry, clock, enforcerKind)
            .RunAsync(resumed, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal("all done", outcome.FinalAnswer);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(1, registry.DispatchedLookups); // the tool really ran after the resume
        Assert.Equal([1, 1, 1], client.ToolsOffered); // every post-resume call was offered the tool
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<AgentOutcome> SuspendAsync(IModelClient client, MutableClock clock, string enforcerKind)
    {
        var state = AgentState.NewTask("test task", new StateBudget
        {
            MaxTurns = 10,
            MaxTotalTokens = 100_000,
            MaxCost = 10m,
            MaxWallClock = Cap
        }, Start);

        var outcome = await BuildHarness(client, new ListingToolRegistry(), clock, enforcerKind)
            .RunAsync(state, CancellationToken.None);

        // Guard the precondition: the suspend itself happened inside the cap.
        Assert.Equal(AgentStatus.AwaitingHuman, outcome.Status);
        return outcome;
    }

    private static HarnessLoop BuildHarness(IModelClient client, IToolRegistry registry, MutableClock clock, string enforcerKind) =>
        new(
            modelClient: client,
            toolRegistry: registry,
            contextBuilder: new StubContextBuilder(),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: enforcerKind switch
            {
                "default" => new BudgetNs.DefaultBudgetEnforcer(clock),
                "turn-scoped" => new BudgetNs.TurnScopedBudgetEnforcer(clock),
                _ => throw new ArgumentOutOfRangeException(nameof(enforcerKind), enforcerKind, null)
            },
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: new NullCheckpointStore(),
            timeProvider: clock);

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    private static ToolCall AskCall() => new(AskCallId, "ask_human", EmptyArgs());

    private static ModelResponse EndTurn(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static ModelResponse ToolUse(params ToolCall[] calls) => new()
    {
        Text = null,
        ToolCalls = calls,
        StopReason = StopReason.ToolUse,
        Usage = Usage.Zero,
        Cost = 0m
    };

    // A clock the test moves by hand, so "the human took five minutes" needs no real waiting.
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    // Scripted responses, plus a record of how many tools each call was offered.
    private sealed class ToolCountingModelClient(params ModelResponse[] responses) : IModelClient
    {
        private readonly Queue<ModelResponse> _responses = new(responses);

        public List<int> ToolsOffered { get; } = [];

        public Task<ModelResponse> CallAsync(
            IReadOnlyList<Message> messages,
            IReadOnlyList<ToolDefinition> availableTools,
            CancellationToken ct)
        {
            ToolsOffered.Add(availableTools.Count);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    // Lists one real tool (StubToolRegistry lists none, which would make "offered no tools" vacuous);
    // ask_human dispatches as a pending result, anything else as a plain success.
    private sealed class ListingToolRegistry : IToolRegistry
    {
        private readonly ITool[] _tools = [new LookupTool()];

        public int DispatchedLookups { get; private set; }

        public IReadOnlyList<ITool> List() => _tools;

        public ITool? Get(string name) => _tools.FirstOrDefault(t => t.Name == name);

        public Task<ToolResult> DispatchAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            if (call.ToolName == "ask_human")
                return Task.FromResult(new ToolResult(call.CallId, "What is your name?", IsPending: true));

            DispatchedLookups++;
            return Task.FromResult(new ToolResult(call.CallId, "found"));
        }
    }

    private sealed class LookupTool : ITool
    {
        public string Name => "lookup";
        public string Description => "Looks something up.";
        public JsonElement InputSchema => EmptyArgs();

        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            Task.FromResult(new ToolResult(call.CallId, "found"));
    }
}
