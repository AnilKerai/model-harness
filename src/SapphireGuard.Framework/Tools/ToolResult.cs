namespace SapphireGuard.Framework.Tools;

/// <summary>Outcome of a tool execution, surfaced back to the model.</summary>
public sealed record ToolResult(string CallId, string Content, bool IsError = false);
