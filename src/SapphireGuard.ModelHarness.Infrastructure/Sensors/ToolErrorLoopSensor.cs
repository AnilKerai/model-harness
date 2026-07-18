using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Opt-in. Flags when the same tool returns an error <paramref name="errorThreshold"/> times in a
/// row (default 3), even when the arguments differ each time — the agent is hammering a tool that
/// keeps failing. The default <c>StuckDetector</c> only fires on identical arguments; this catches
/// "the search keeps failing however I phrase it". Hooks <see cref="HookPoint.PostToolCall"/>
/// (advisory — the result is already recorded), so it annotates the trajectory and the model can
/// replan. Not registered by default; opt in with <c>builder.WithSensor&lt;ToolErrorLoopSensor&gt;()</c>.
/// </summary>
public sealed class ToolErrorLoopSensor(int errorThreshold = 3) : ISensor
{
    public string Name => "tool-error-loop";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PostToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ToolCallStep { Result.IsError: true } current)
            return Task.FromResult(SensorResult.Pass);

        var toolName = current.Call.ToolName;
        var consecutive = 0;
        for (var i = state.Trajectory.Count - 1; i >= 0; i--)
        {
            if (state.Trajectory[i] is UserMessageStep)
                break;                         // a new user turn is a fresh intent — don't scan past it
            if (state.Trajectory[i] is not ToolCallStep prior)
                continue;                      // other non-tool steps are transparent
            if (prior.Call.ToolName != toolName)
                break;                         // a different tool breaks the streak
            if (!prior.Result.IsError)
                break;                         // a success breaks the streak
            if (++consecutive >= errorThreshold)
                return Task.FromResult(SensorResult.Intervene(
                    $"Tool '{toolName}' has failed {consecutive} times in a row. Stop retrying it — " +
                    "the approach isn't working; try a different tool or rethink the task."));
        }

        return Task.FromResult(SensorResult.Pass);
    }
}
