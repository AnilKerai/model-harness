using System.Text.Json;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string CallId, string ToolName, JsonElement Arguments);
