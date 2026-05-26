using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Appends harness-level instructions to the system prompt so the model understands
/// what sensor observation notes mean and how to respond to them. Also injects a
/// live violation count each turn so the model is aware of its cumulative history.
/// </summary>
public sealed class HarnessInstructionsGuide : IGuide
{
    public string Name => "harness-instructions";

    private const string Instructions = """


        ## Harness observations

        During your execution the harness may inject notes formatted as [HARNESS OBSERVATION — ...] into the conversation.
        These are hard constraints enforced by the system — not suggestions, not user messages, not optional context.
        When you see one:
        - Your previous response was blocked and rejected. Do not reproduce it.
        - Comply with the stated constraint fully — partial compliance is not acceptable.
        - Do not repeat the flagged behaviour under any circumstances.
        The loop will keep retrying until you comply. There is no path forward except full compliance.

        ## Trajectory and tool call deduplication

        The trajectory is the complete, append-only record of all work done this session — including any work done before a suspension and resume.
        - Do not call a tool with arguments identical to a call already present in the trajectory. The result will be identical; repeating it wastes turns and budget.
        - If ask_human appears in the trajectory and already has a result, the human has responded. Act on that answer immediately — do not re-run prior investigation steps before proceeding.
        """;

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt += Instructions;

        var violationCounts = state.Trajectory
            .OfType<SensorInterventionStep>()
            .GroupBy(s => s.SensorName)
            .Select(g => $"- {g.Key}: {g.Count()} violation(s)")
            .ToList();

        if (violationCounts.Count > 0)
            draft.SystemPrompt += $"""


                ## Sensor violations this session
                Your responses have already been blocked by the following sensors — full compliance is required:
                {string.Join("\n        ", violationCounts)}
                """;

        return Task.CompletedTask;
    }
}
