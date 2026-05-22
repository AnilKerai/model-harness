namespace SapphireGuard.Framework.State;

/// <summary>Why the model stopped producing tokens for a given response.</summary>
public enum StopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    StopSequence,
    Other
}
