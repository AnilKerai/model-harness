using System.Text;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Renders the available tools into a "# Available tools" system section. Runs after
/// <see cref="ToolSelectorGuide"/> so it reflects the final, filtered tool set. The
/// structured tool definitions still flow to the model separately via
/// <see cref="ContextDraft.AvailableTools"/>; this is the human-readable catalogue.
/// </summary>
public sealed class ToolCatalogueGuide : IGuide
{
    public string Name => "tool-catalogue";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        if (draft.AvailableTools.Count == 0)
            return Task.CompletedTask;

        var sb = new StringBuilder();
        sb.AppendLine("# Available tools");
        foreach (var t in draft.AvailableTools)
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);

        draft.SystemSections.Add(sb.ToString().TrimEnd());
        return Task.CompletedTask;
    }
}
