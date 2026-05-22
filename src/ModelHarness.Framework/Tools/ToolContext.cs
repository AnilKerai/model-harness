namespace ModelHarness.Framework.Tools;

/// <summary>Ambient context handed to a tool during execution.</summary>
public sealed record ToolContext(string TaskId, string CallId, IReadOnlyDictionary<string, string> Metadata)
{
    public static ToolContext Empty(string taskId, string callId) =>
        new(taskId, callId, new Dictionary<string, string>());
}
