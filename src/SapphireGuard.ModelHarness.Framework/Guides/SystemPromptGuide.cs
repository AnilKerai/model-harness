using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Establishes the caller's base system prompt on the draft. Registered via
/// <c>WithSystemPrompt</c> inside the configure callback, so it runs <em>after</em> the default
/// pipeline — including <see cref="HarnessInstructionsGuide"/> and <see cref="ReActGuide"/>, which
/// append to the system prompt with <c>+=</c>. It therefore <b>prepends</b> rather than assigns:
/// assigning would clobber those contributions; prepending keeps the caller's prompt first and is
/// correct regardless of registration order.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class SystemPromptGuide(string systemPrompt) : IGuide
{
    public string Name => "system-prompt";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt = systemPrompt + draft.SystemPrompt;
        return Task.CompletedTask;
    }
}
