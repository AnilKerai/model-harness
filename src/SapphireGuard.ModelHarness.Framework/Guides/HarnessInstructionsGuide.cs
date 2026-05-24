using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Appends harness-level instructions to the system prompt so the model understands
/// what sensor observation notes mean and how to respond to them.
/// </summary>
[ExcludeFromCodeCoverage]
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
        """;

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt += Instructions;
        return Task.CompletedTask;
    }
}
