using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class HarnessInstructionsGuideTests
{
    private const string ViolationsHeading = "Sensor violations";

    private static AgentState EmptyState() =>
        AgentState.NewTask("test", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static SensorInterventionStep Intervention(HookPoint hookPoint, string sensorName) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, hookPoint, sensorName, "reason", TriggeringStep: null);

    private static async Task<string> PromptFor(AgentState state)
    {
        var draft = new ContextDraft();
        await new HarnessInstructionsGuide().ContributeAsync(draft, state, CancellationToken.None);
        return draft.SystemPrompt;
    }

    // The lines under the "Sensor violations" heading, with the heading and its sentence dropped.
    private static string[] ViolationLines(string prompt) =>
        prompt[prompt.IndexOf(ViolationsHeading, StringComparison.Ordinal)..]
            .Split('\n')
            .Skip(2)
            .ToArray();

    // ── Instruction text ──────────────────────────────────────────────────────

    [Fact]
    public async Task Instructions_DescribeBothBlockingAndAdvisoryNotes()
    {
        var prompt = await PromptFor(EmptyState());

        Assert.Contains("Some notes block something", prompt);
        Assert.Contains("Other notes are advisory and block nothing", prompt);
        Assert.Contains("[HARNESS OBSERVATION", prompt);
    }

    [Theory]
    [InlineData("- PostModelCall: your previous response was rejected")]
    [InlineData("- PreReturn: your answer was challenged")]
    [InlineData("- PreToolCall: the tool call was blocked and never ran")]
    [InlineData("- PreModelCall: guidance")]
    [InlineData("- PostToolCall: a flag on a tool result that has already run")]
    public async Task Instructions_WordEachHookpointForWhatItDoes(string expected)
    {
        Assert.Contains(expected, await PromptFor(EmptyState()));
    }

    [Fact]
    public async Task Instructions_DoNotClaimEveryNoteBlockedTheResponseOrThatTheLoopRetriesForever()
    {
        // Advisory hookpoints block nothing, and the loop gives up after maxConsecutiveInterventions
        // and returns a PartialResult — neither claim may come back.
        var prompt = await PromptFor(EmptyState());

        Assert.DoesNotContain("blocked and rejected", prompt);
        Assert.DoesNotContain("keep retrying", prompt);
        Assert.DoesNotContain("no path forward", prompt);
    }

    [Fact]
    public async Task Contribute_AppendsToTheExistingSystemPrompt()
    {
        var draft = new ContextDraft { SystemPrompt = "You are an agent." };

        await new HarnessInstructionsGuide().ContributeAsync(draft, EmptyState(), CancellationToken.None);

        Assert.StartsWith("You are an agent.", draft.SystemPrompt);
        Assert.Contains("Harness observations", draft.SystemPrompt);
    }

    // ── Violations list ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoInterventions_HasNoViolationsSection()
    {
        Assert.DoesNotContain(ViolationsHeading, await PromptFor(EmptyState()));
    }

    [Fact]
    public async Task AdvisoryOnlyTrajectory_HasNoViolationsSection()
    {
        // ProgressCheckSensor nudges every 5 turns at PreModelCall and the PostToolCall sensors flag
        // results — none of that blocks a response, so none of it is a violation.
        var state = EmptyState()
            .AppendStep(Intervention(HookPoint.PreModelCall, "progress-check"))
            .AppendStep(Intervention(HookPoint.PostToolCall, "prompt-injection"))
            .AppendStep(Intervention(HookPoint.PostToolCall, "taint-tracking"));

        Assert.DoesNotContain(ViolationsHeading, await PromptFor(state));
    }

    [Fact]
    public async Task TwoBlockingInterventionsFromDifferentSensors_ListsBothAtColumnZero()
    {
        var state = EmptyState()
            .AppendStep(Intervention(HookPoint.PostModelCall, "pii-redaction"))
            .AppendStep(Intervention(HookPoint.PreReturn, "critic"));

        var lines = ViolationLines(await PromptFor(state));

        // Every item, not just the first: interpolated values skip the raw string's indentation
        // stripping, and the join separator used to put 8 spaces in front of the second and later items.
        Assert.Equal(["- pii-redaction: 1 violation(s)", "- critic: 1 violation(s)"], lines);
    }

    [Fact]
    public async Task ThreeBlockingSensors_EveryItemStartsWithADash()
    {
        var state = EmptyState()
            .AppendStep(Intervention(HookPoint.PostModelCall, "a"))
            .AppendStep(Intervention(HookPoint.PreToolCall, "b"))
            .AppendStep(Intervention(HookPoint.PreReturn, "c"));

        var lines = ViolationLines(await PromptFor(state));

        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("- ", l));
    }

    [Theory]
    [InlineData(HookPoint.PostModelCall)]
    [InlineData(HookPoint.PreToolCall)]
    [InlineData(HookPoint.PreReturn)]
    public async Task EachBlockingHookpoint_IsCounted(HookPoint hookPoint)
    {
        var state = EmptyState().AppendStep(Intervention(hookPoint, "s"));

        Assert.Equal(["- s: 1 violation(s)"], ViolationLines(await PromptFor(state)));
    }

    [Theory]
    [InlineData(HookPoint.PreModelCall)]
    [InlineData(HookPoint.PostToolCall)]
    public async Task EachAdvisoryHookpoint_IsNotCounted(HookPoint hookPoint)
    {
        var state = EmptyState().AppendStep(Intervention(hookPoint, "s"));

        Assert.DoesNotContain(ViolationsHeading, await PromptFor(state));
    }

    [Fact]
    public async Task MixedTrajectory_CountsOnlyTheBlockingInterventions()
    {
        // "critic" blocks twice and flags nothing; "progress-check" and "taint-tracking" only advise.
        // taint-tracking also blocks once at PreToolCall, so it appears — with a count of 1, not 3.
        var state = EmptyState()
            .AppendStep(Intervention(HookPoint.PreModelCall, "progress-check"))
            .AppendStep(Intervention(HookPoint.PreReturn, "critic"))
            .AppendStep(Intervention(HookPoint.PostToolCall, "taint-tracking"))
            .AppendStep(Intervention(HookPoint.PreToolCall, "taint-tracking"))
            .AppendStep(Intervention(HookPoint.PostToolCall, "taint-tracking"))
            .AppendStep(Intervention(HookPoint.PreReturn, "critic"));

        var lines = ViolationLines(await PromptFor(state));

        Assert.Equal(["- critic: 2 violation(s)", "- taint-tracking: 1 violation(s)"], lines);
    }

    [Fact]
    public async Task ViolationsFromEarlierUserTurns_AreStillListed()
    {
        // The list is a session-wide record ("this session"), not scoped to the latest user message the
        // way the loop's own intervention counters are: in a chat an early block is still reported.
        var state = EmptyState()
            .AppendStep(Intervention(HookPoint.PostModelCall, "pii-redaction"))
            .AppendStep(new UserMessageStep(Guid.NewGuid(), DateTimeOffset.UtcNow, "a new question"));

        Assert.Equal(["- pii-redaction: 1 violation(s)"], ViolationLines(await PromptFor(state)));
    }

    [Fact]
    public async Task ViolationsSection_FramesTheInterventionsAsBlockedOrChallenged()
    {
        var state = EmptyState().AppendStep(Intervention(HookPoint.PostModelCall, "s"));

        Assert.Contains("have already blocked or challenged your work", await PromptFor(state));
    }
}
