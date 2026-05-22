namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>Lifecycle status of an agent run.</summary>
public enum AgentStatus
{
    Running,
    Done,
    Failed,
    AwaitingHuman,
    PartialResult
}
