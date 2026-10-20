using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Appends harness-level instructions to the system prompt so the model understands
/// what sensor observation notes mean and how to respond to them: a note from a blocking
/// hookpoint rejected, challenged or blocked something, while an advisory one blocked nothing.
/// Also injects a live count each turn of the blocking interventions so far in the trajectory
/// (advisory notes are not violations), so the model is aware of its cumulative history.
/// </summary>
public sealed class HarnessInstructionsGuide : IGuide
{
    public string Name => "harness-instructions";

    private const string Instructions = """


        ## Harness observations

        During your execution the harness may inject notes formatted as [HARNESS OBSERVATION — <sensor> at <hookpoint>] into the conversation.
        They come from the system, not the user. The hookpoint says what the harness did, and so what you should do.

        Some notes block something. They are hard constraints enforced by the system — not suggestions, not optional context. Comply with the stated constraint fully (partial compliance is not acceptable) and do not repeat the flagged behaviour:
        - PostModelCall: your previous response was rejected and removed from the conversation. Do not reproduce it; respond again within the constraint.
        - PreReturn: your answer was challenged and not accepted. It is still visible above; revise it to address the note, then give the corrected final answer.
        - PreToolCall: the tool call was blocked and never ran; its result carries the same reason. Do not retry the same call; plan a different approach.

        Other notes are advisory and block nothing:
        - PreModelCall: guidance for the turn you are about to take. Take it into account and carry on.
        - PostToolCall: a flag on a tool result that has already run and is in the conversation. Treat that result with the caution the note describes, and carry on.

        ## Trajectory and tool call deduplication

        The trajectory is the complete, append-only record of all work done this session — including any work done before a suspension and resume.
        - Do not call a tool with arguments identical to a call already present in the trajectory. The result will be identical; repeating it wastes turns and budget.
        - If ask_human appears in the trajectory and already has a result, the human has responded. Act on that answer immediately — do not re-run prior investigation steps before proceeding.
        """;

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt += Instructions;

        // Advisory notes block nothing, so they are not violations. The count spans the whole
        // trajectory ("this session"), so in a chat an early block is still listed on later user turns.
        var violationCounts = state.Trajectory
            .OfType<SensorInterventionStep>()
            .Where(s => !s.HookPoint.IsAdvisory())
            .GroupBy(s => s.SensorName)
            .Select(g => $"- {g.Key}: {g.Count()} violation(s)")
            .ToList();

        // An interpolated value is inserted verbatim — the raw string's indentation stripping does not
        // reach it — so the separator must be a bare newline, or every item after the first gains
        // leading spaces and renders as a markdown code block.
        if (violationCounts.Count > 0)
            draft.SystemPrompt += $"""


                ## Sensor violations this session
                The following sensors have already blocked or challenged your work this session — avoid repeating the flagged behaviour:
                {string.Join("\n", violationCounts)}
                """;

        return Task.CompletedTask;
    }
}
