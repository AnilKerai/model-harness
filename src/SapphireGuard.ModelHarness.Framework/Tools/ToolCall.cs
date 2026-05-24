using System.Text.Json;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string CallId, string ToolName, JsonElement Arguments)
{
    /// <summary>Unique identifier for this invocation, used to correlate results back to the model.</summary>
    public string CallId { get; } = CallId;

    /// <summary>Name of the tool to invoke, as declared in <see cref="ITool.Name"/>.</summary>
    public string ToolName { get; } = ToolName;

    /// <summary>Arguments provided by the model, conforming to <see cref="ITool.InputSchema"/>.</summary>
    public JsonElement Arguments { get; } = Arguments;
}
