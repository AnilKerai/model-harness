using System.Text.Json;

namespace AgentHarness.Framework.Tools;

/// <summary>A unit of capability the agent can invoke.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct);
}
