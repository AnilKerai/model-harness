using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

internal static class ClaudeWireFormat
{
    // Values from https://docs.claude.com/en/api/handling-stop-reasons. Anything unlisted maps to Other, never
    // EndTurn, so the loop's allowlist keeps it out of Done. pause_turn only follows a server-tool loop, and this
    // adapter declares client tools only, so it cannot arrive today; if it ever does, the response is incomplete
    // by definition and Other is correct.
    public static StopReason MapStopReason(string? raw) => raw switch
    {
        "end_turn" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "refusal" => StopReason.Refusal,
        "model_context_window_exceeded" => StopReason.ContextWindowExceeded,
        _ => StopReason.Other
    };
}
