using System.Text.Json;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>A tool invocation requested by the model.</summary>
/// <param name="CallId">Unique identifier for this invocation, used to correlate results back to the model.</param>
/// <param name="ToolName">Name of the tool to invoke, as declared in <see cref="ITool.Name"/>.</param>
/// <param name="Arguments">Arguments provided by the model, conforming to <see cref="ITool.InputSchema"/>.</param>
public sealed record ToolCall(string CallId, string ToolName, JsonElement Arguments);
