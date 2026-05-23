using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

/// <summary>
/// The model's write access to its procedural memory. <c>save</c> upserts a skill
/// (create or overwrite); <c>delete</c> removes one. The model decides when to call
/// this — typically after solving a non-trivial, multi-step task — so capture is
/// agent-initiated, not forced by the loop.
/// </summary>
public sealed class SkillManageTool(ISkillStore store) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action":      { "type": "string", "enum": ["save", "delete"] },
            "name":        { "type": "string", "description": "Unique skill name (kebab-case)." },
            "description": { "type": "string", "description": "One line describing what the skill does." },
            "when_to_use": { "type": "string", "description": "One line: the situation this skill applies to." },
            "body":        { "type": "string", "description": "The full procedure in markdown: steps, pitfalls, verification." },
            "version":     { "type": "string", "description": "Optional semantic version; defaults to 1.0.0." }
          },
          "required": ["action", "name"]
        }
        """).RootElement;

    public string Name => "skill_manage";

    public string Description =>
        "Save or delete a reusable skill (procedural memory). Save the working approach " +
        "after solving a non-trivial task so it can be reused later; deleting removes a stale skill.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var args = call.Arguments;
        var action = Str(args, "action");
        var name = Str(args, "name");

        if (string.IsNullOrWhiteSpace(name))
            return Error(call, "A non-empty 'name' is required.");

        switch (action)
        {
            case "save":
                var description = Str(args, "description");
                var whenToUse = Str(args, "when_to_use");
                var body = Str(args, "body");
                if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(whenToUse) || string.IsNullOrWhiteSpace(body))
                    return Error(call, "'save' requires 'description', 'when_to_use', and 'body'.");

                var version = Str(args, "version");
                await store.SaveAsync(
                    new Skill(name, description, whenToUse, body,
                        string.IsNullOrWhiteSpace(version) ? "1.0.0" : version),
                    ct);
                return new ToolResult(call.CallId, $"Saved skill '{name}'.");

            case "delete":
                await store.DeleteAsync(name, ct);
                return new ToolResult(call.CallId, $"Deleted skill '{name}'.");

            default:
                return Error(call, $"Unknown action '{action}'. Use 'save' or 'delete'.");
        }
    }

    private static string Str(JsonElement args, string prop) =>
        args.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static ToolResult Error(ToolCall call, string message) =>
        new(call.CallId, message, IsError: true);
}
