using System.Text;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Surfaces the agent's procedural memory: lists each stored skill by name and when
/// to use it (the cheap catalogue), so the model knows what it can reuse. The full
/// procedure is pulled on demand via the skill-view tool. Emits nothing when no skills
/// exist, so the default <see cref="NullSkillStore"/> adds zero overhead.
/// </summary>
public sealed class SkillsGuide(ISkillStore store) : IGuide
{
    public string Name => "skills";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        var skills = await store.ListAsync(ct);
        if (skills.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("# Available skills");
        sb.AppendLine("Reusable procedures from past work. Load the full procedure with the skill-view tool before applying one.");
        foreach (var s in skills)
            sb.Append("- ").Append(s.Name).Append(" — ").AppendLine(s.WhenToUse);

        draft.SystemSections.Add(sb.ToString().TrimEnd());
    }
}
