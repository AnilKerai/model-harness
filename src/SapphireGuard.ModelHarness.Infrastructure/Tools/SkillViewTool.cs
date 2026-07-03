using System.Text;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

/// <summary>
/// Loads the full procedure for a single skill on demand (progressive disclosure): the skills
/// catalogue shows the model what exists; this loads the body when it picks one. The body is
/// <b>pinned</b> into the persistent context region (via <see cref="ToolResult.Pins"/>) rather than
/// returned as an evictable tool result, so a loaded procedure or output contract survives
/// compaction — progressive disclosure without losing the loaded content to eviction.
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

        // Pin the body into the persistent context region so it survives compaction, and return a
        // short ack rather than the (evictable) body — progressive disclosure without eviction loss.
        return new ToolResult(
            call.CallId,
            $"Loaded skill '{skill.Name}'. Its full guidance is now pinned in your context under \"Skill: {skill.Name}\".",
            Pins: [new PinnedNote($"Skill: {skill.Name}", sb.ToString().TrimEnd())]);
    }
}
