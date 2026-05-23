using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SkillLearning;

// Deterministic, no-API-key model that demonstrates the skill read/write loop.
// Its behaviour is a pure function of context: save a skill when none exists, load
// one with skill_view once the SkillsGuide has surfaced it, then reuse the loaded
// procedure. Run 2's reuse is therefore *caused* by run 1's persisted skill.
public sealed class SkillScriptedModelClient : IModelClient
{
    private const string SkillName = "warm-greeting";
    private const string BodyMarker = "Offer further help";

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var lastToolResult = messages.LastOrDefault(m => m.Role == MessageRole.Tool)?.Content ?? "";
        // The skill name appears in a system message only once the SkillsGuide has
        // rendered the catalogue — so this is true exactly when the skill exists.
        var skillAvailable = messages.Any(m => m.Role == MessageRole.System && m.Content.Contains(SkillName));

        ModelResponse response =
            lastToolResult.Contains(BodyMarker) ? Reuse()   // skill body just loaded → answer with it
            : skillAvailable ? ViewSkill()                  // skill exists but not yet loaded → load it
            : SaveSkill();                                  // no skill yet → capture one

        return Task.FromResult(response);
    }

    private static ModelResponse SaveSkill()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            action = "save",
            name = SkillName,
            description = "Produce a warm, friendly greeting",
            when_to_use = "when asked to greet a user",
            body = "1. Address the user kindly.\n2. Wish them well.\n3. Offer further help."
        });
        return ToolTurn("No matching skill yet — I'll capture the approach for next time.", "skill_manage", args);
    }

    private static ModelResponse ViewSkill()
    {
        var args = JsonSerializer.SerializeToElement(new { name = SkillName });
        return ToolTurn("A relevant skill exists — loading it before I answer.", "skill_view", args);
    }

    private static ModelResponse Reuse() => Final(
        "Hello, friend — I hope your day is going wonderfully. How can I help? [reused the warm-greeting skill]");

    private static ModelResponse ToolTurn(string text, string toolName, JsonElement args) => new()
    {
        Text = text,
        ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), toolName, args)],
        StopReason = StopReason.ToolUse,
        Usage = new Usage(100, 20),
        Cost = 0m
    };

    private static ModelResponse Final(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = new Usage(80, 15),
        Cost = 0m
    };
}
