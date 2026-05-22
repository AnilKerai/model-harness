namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>Token usage reported by the model client for a single call.</summary>
public sealed record Usage(int InputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + OutputTokens;

    public static Usage Zero { get; } = new(0, 0);
}
