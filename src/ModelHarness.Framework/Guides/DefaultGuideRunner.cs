using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Guides;

/// <summary>
/// Runs guides sequentially in registration order. Sequential execution lets
/// each guide build on contributions from the ones before it — a tool-selector
/// guide can, for example, inspect memory snippets added by a memory guide.
/// </summary>
public sealed class DefaultGuideRunner(IEnumerable<IGuide> guides) : IGuideRunner
{
    private readonly IReadOnlyList<IGuide> _guides = [.. guides];

    public async Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct)
    {
        var draft = new ContextDraft { AvailableTools = [.. allTools] };

        foreach (var guide in _guides)
        {
            ct.ThrowIfCancellationRequested();
            await guide.ContributeAsync(draft, state, ct);
        }

        return draft;
    }
}
