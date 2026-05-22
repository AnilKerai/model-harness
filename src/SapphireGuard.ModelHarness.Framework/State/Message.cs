namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>A single message in the prompt sent to the model.</summary>
public sealed record Message(MessageRole Role, string Content);

public enum MessageRole
{
    System,
    User,
    Assistant,
    /// <summary>A tool invocation request emitted by the model (call + args).</summary>
    ToolUse,
    /// <summary>The result returned by a tool after execution.</summary>
    Tool
}
