using System.Text.Json;

namespace SapphireGuard.Framework.Tools;

/// <summary>
/// Model-facing description of a tool. The harness projects <see cref="ITool"/>
/// instances into this record before handing them to <see cref="Model.IModelClient"/>,
/// keeping the model client unaware of the tool execution abstraction.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);
