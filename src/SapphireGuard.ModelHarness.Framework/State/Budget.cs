namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// Hard limits enforced at the top of every loop turn. When any limit is exceeded the
/// loop budget-finalises: one last model call with tools disabled, then
/// <see cref="AgentStatus.PartialResult"/>.
/// </summary>
public sealed record Budget
{
    /// <summary>Maximum number of loop turns before budget finalisation.</summary>
    public required int MaxTurns { get; init; }

    /// <summary>Maximum cumulative context tokens before budget finalisation.</summary>
    public required int MaxContextTokens { get; init; }

    /// <summary>Maximum cumulative spend in USD before budget finalisation.</summary>
    public required decimal MaxCostUsd { get; init; }

    /// <summary>Maximum wall-clock duration from task start before budget finalisation.</summary>
    public required TimeSpan MaxWallClock { get; init; }
}
