namespace SapphireGuard.Framework.State;

/// <summary>Hard limits enforced by <see cref="SapphireGuard.Framework.Budget.IBudgetEnforcer"/> during a run.</summary>
public sealed record Budget
{
    public required int MaxTurns { get; init; }
    public required int MaxContextTokens { get; init; }
    public required decimal MaxCostUsd { get; init; }
    public required TimeSpan MaxWallClock { get; init; }
}
