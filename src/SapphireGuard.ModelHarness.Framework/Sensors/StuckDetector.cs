using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>
/// Flags when the agent issues the same tool call (name + arguments) three or
/// more times in a row, which usually means it is failing to integrate the
/// tool's output and looping.
/// </summary>
public sealed class StuckDetector(int repeatThreshold = 3) : ISensor
{
    public string Name => "stuck-detector";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ToolCallStep current)
        {
            return Task.FromResult(SensorResult.Pass);
        }

        var key = SignatureOf(current);
        var consecutive = 1;
        for (var i = state.Trajectory.Count - 1; i >= 0; i--)
        {
            if (state.Trajectory[i] is not ToolCallStep prior)
            {
                continue;
            }
            if (SignatureOf(prior) != key)
            {
                break;
            }
            consecutive++;
            if (consecutive >= repeatThreshold)
            {
                return Task.FromResult(SensorResult.Intervene(
                    $"Tool '{current.Call.ToolName}' was invoked {consecutive} times in a row with identical arguments. Try a different approach."));
            }
        }

        return Task.FromResult(SensorResult.Pass);
    }

    private static string SignatureOf(ToolCallStep step) =>
        $"{step.Call.ToolName}::{step.Call.Arguments.GetRawText()}";
}
