using System.Text.Json;

namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>A unit of capability the agent can invoke by name.</summary>
public interface ITool
{
    /// <summary>Unique identifier the model uses to invoke this tool.</summary>
    string Name { get; }

    /// <summary>Natural-language description shown to the model in the tool catalogue.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the tool's input parameters, sent to the model as part of the tool definition.</summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Executes the tool and returns its result. Return a <see cref="ToolResult"/> with
    /// <c>IsError = true</c> to signal a tool-side error to the model rather than throwing.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct);
}
