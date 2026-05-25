namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// Describes the human-input request that caused the harness to suspend with
/// <see cref="AgentStatus.AwaitingHuman"/>. Carried by <see cref="AgentOutcome.PendingHumanInput"/>.
/// </summary>
public sealed record PendingHumanInput(string CallId, string Question);
