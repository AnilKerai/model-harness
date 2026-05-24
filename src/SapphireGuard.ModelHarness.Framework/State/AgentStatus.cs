namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>Lifecycle status of an agent run.</summary>
public enum AgentStatus
{
    /// <summary>The run is in progress.</summary>
    Running,

    /// <summary>The model produced a final answer and the run completed successfully.</summary>
    Done,

    /// <summary>The run terminated due to an unrecoverable error.</summary>
    Failed,

    /// <summary>The run is paused waiting for a human response via <see cref="SapphireGuard.ModelHarness.Framework.Tools.IHumanChannel"/>.</summary>
    AwaitingHuman,

    /// <summary>
    /// Budget was exhausted before the model could finalise naturally. The loop made
    /// one final model call with tools disabled and returned its best answer.
    /// </summary>
    PartialResult
}
