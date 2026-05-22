using SapphireGuard.Framework.State;

namespace SapphireGuard.Framework.Guides;

/// <summary>
/// Renders the agent's trajectory into <see cref="ContextDraft.TrajectoryMessages"/>.
/// Each step type has a distinct rendering: model turns as assistant messages,
/// tool calls and results as paired messages, sensor interventions as system
/// notes so the model can re-plan without them polluting tool-call history.
/// </summary>
public sealed class TrajectoryGuide : IGuide
{
    public string Name => "trajectory";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        foreach (var step in state.Trajectory)
        {
            switch (step)
            {
                case ModelCallStep mc:
                    if (!string.IsNullOrEmpty(mc.Response.Text))
                    {
                        draft.TrajectoryMessages.Add(new Message(MessageRole.Assistant, mc.Response.Text));
                    }
                    break;

                case ToolCallStep tc:
                    draft.TrajectoryMessages.Add(new Message(MessageRole.Assistant,
                        $"[tool_call name={tc.Call.ToolName} id={tc.Call.CallId}] {tc.Call.Arguments.GetRawText()}"));
                    draft.TrajectoryMessages.Add(new Message(MessageRole.Tool,
                        $"[tool_result id={tc.Result.CallId} error={tc.Result.IsError}] {tc.Result.Content}"));
                    break;

                case SensorInterventionStep si:
                    draft.TrajectoryMessages.Add(new Message(MessageRole.System,
                        $"[sensor:{si.SensorName} at {si.HookPoint}] {si.Reason} — adjust your plan accordingly."));
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
