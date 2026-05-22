using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>Sets the system prompt on the draft. Should run first.</summary>
[ExcludeFromCodeCoverage]
public sealed class SystemPromptGuide(string systemPrompt) : IGuide
{
    public string Name => "system-prompt";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt = systemPrompt;
        return Task.CompletedTask;
    }
}
