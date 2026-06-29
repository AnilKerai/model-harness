namespace SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

// Pure parsers for the wire format the harness stores tool calls/results in (mirrors
// ClaudeModelClient's format). Kept out of the I/O client so the parsing — the highest-risk
// string-handling in the adapter — is unit-testable and counts toward coverage.
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
}
