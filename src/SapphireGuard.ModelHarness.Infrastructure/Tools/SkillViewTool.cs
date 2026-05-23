using System.Text;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

/// <summary>
/// Loads the full procedure for a single skill on demand (progressive disclosure):
/// the skills catalogue shows the model what exists; this returns the body when it
/// picks one.
/// </summary>
public sealed class SkillViewTool(ISkillStore store) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The name of the skill to load." }
          },
          "required": ["name"]
        }
        """).RootElement;

    public string Name => "skill_view";

    public string Description =>
        "Load the full procedure for a named skill before applying it. " +
        "Names come from the '# Available skills' section.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var name = call.Arguments.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name))
            return new ToolResult(call.CallId, "A non-empty 'name' is required.", IsError: true);

        var skill = await store.GetAsync(name, ct);
        if (skill is null)
            return new ToolResult(call.CallId, $"Skill '{name}' not found.", IsError: true);

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(skill.Name);
        sb.Append("Description: ").AppendLine(skill.Description);
        sb.Append("When to use: ").AppendLine(skill.WhenToUse);
        sb.AppendLine();
        sb.AppendLine(skill.Body);

        return new ToolResult(call.CallId, sb.ToString().TrimEnd());
    }
}
