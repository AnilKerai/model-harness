namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>Lifecycle positions at which sensors can observe and intervene in a loop turn.</summary>
public enum HookPoint
{
    /// <summary>
    /// Before context is built and the model is called. Interventions here force-finalise
    /// the run immediately — the loop cannot loop back because no model call has happened
    /// yet and the trajectory is unchanged from the previous turn.
    /// </summary>
    PreModelCall,

    /// <summary>
    /// After the model responds, before acting on its output. Interventions loop back for
    /// a retry; the flagged response is suppressed from the next context so the model
    /// cannot re-see it.
    /// </summary>
    PostModelCall,

    /// <summary>
    /// Before a tool is dispatched. Interventions block the call entirely; a
    /// <see cref="State.ToolCallStep"/> with <c>IsError = true</c> is recorded so the
    /// model sees a clean error and can replan.
    /// </summary>
    PreToolCall,

    /// <summary>
    /// After a tool result is received. Advisory only — the result is already in the
    /// trajectory. Use <see cref="PreToolCall"/> if you need to prevent execution.
    /// </summary>
    PostToolCall,

    /// <summary>
    /// Before the final answer is returned to the caller. Interventions loop back for a
    /// retry; the prior answer remains visible in context so the model can correct it.
    /// </summary>
    PreReturn
}
