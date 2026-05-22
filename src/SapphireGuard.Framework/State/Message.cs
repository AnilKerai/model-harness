namespace SapphireGuard.Framework.State;

/// <summary>A single message in the prompt sent to the model.</summary>
public sealed record Message(MessageRole Role, string Content);

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}
