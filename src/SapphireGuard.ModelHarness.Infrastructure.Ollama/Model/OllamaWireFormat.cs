using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

// Pure mapping between the harness and Ollama's wire format: parsers for the format the harness
// stores tool calls/results in (mirrors ClaudeModelClient's), and the done_reason mapping. Kept out
// of the I/O client so the branching logic is unit-testable and counts toward coverage.
internal static class OllamaWireFormat
{
    // Format: "[tool_call name={name} id={callId}] {argsJson}"
    public static (string Name, string CallId, string ArgsJson) ParseToolUse(string content)
    {
        string name = "", callId = "", argsJson = "{}";

        if (!content.StartsWith("[tool_call ", StringComparison.Ordinal))
            return (name, callId, argsJson);

        var closeBracket = content.IndexOf(']');
        if (closeBracket <= 0)
            return (name, callId, argsJson);

        var meta = content[1..closeBracket];
        argsJson = content[(closeBracket + 1)..].TrimStart();

        foreach (var part in meta.Split(' '))
        {
            if (part.StartsWith("name=", StringComparison.Ordinal)) name = part[5..];
            if (part.StartsWith("id=", StringComparison.Ordinal)) callId = part[3..];
        }

        return (name, callId, argsJson);
    }

    // Format: "[tool_result id={callId} error={bool}] {resultText}"
    public static (string CallId, bool IsError, string ResultText) ParseToolResult(string content)
    {
        string callId = "";
        bool isError = false;
        var resultText = content;

        if (!content.StartsWith("[tool_result ", StringComparison.Ordinal))
            return (callId, isError, resultText);

        var closeBracket = content.IndexOf(']');
        if (closeBracket <= 0)
            return (callId, isError, resultText);

        var meta = content[1..closeBracket];
        resultText = content[(closeBracket + 1)..].TrimStart();

        foreach (var part in meta.Split(' '))
        {
            if (part.StartsWith("id=", StringComparison.Ordinal)) callId = part[3..];
            if (part.StartsWith("error=", StringComparison.Ordinal))
                isError = string.Equals(part[6..], "true", StringComparison.OrdinalIgnoreCase);
        }

        return (callId, isError, resultText);
    }

    // done_reason values from Ollama's llm/server.go: "stop", "length", "connection_closed", plus "load"/"unload"
    // for model-lifecycle requests. Ollama reports "stop" even on a response carrying tool calls, so ToolUse is
    // inferred from their presence. A missing value — a server that predates the field — falls back to that same
    // inference, which is what this adapter did before it read the field. Any other value is Other, never EndTurn,
    // so the loop's allowlist keeps it out of Done.
    public static StopReason MapStopReason(string? doneReason, bool hasToolCalls) => doneReason switch
    {
        null or "" or "stop" => hasToolCalls ? StopReason.ToolUse : StopReason.EndTurn,
        "length" => StopReason.MaxTokens,
        _ => StopReason.Other
    };
}
