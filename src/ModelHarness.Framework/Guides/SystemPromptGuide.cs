using ModelHarness.Framework.State;

namespace ModelHarness.Framework.Guides;

/// <summary>Sets the system prompt on the draft. Should run first.</summary>
public sealed class SystemPromptGuide(string systemPrompt) : IGuide
{
    public string Name => "system-prompt";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt = systemPrompt;
        return Task.CompletedTask;
    }
}
