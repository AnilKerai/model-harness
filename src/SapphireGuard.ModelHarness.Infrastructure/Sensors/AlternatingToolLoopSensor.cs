using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Opt-in. Flags when the agent ping-pongs between two distinct tool calls — A, B, A, B — for
/// <paramref name="minCycles"/> full cycles (default 2). The default <c>StuckDetector</c> only
/// catches the same call repeated consecutively; this catches two calls alternating, e.g.
/// write-then-read-then-write the same value. Hooks <see cref="HookPoint.PreToolCall"/>, so the
/// next call is blocked and the model must replan. Not registered by default; opt in with
/// <c>builder.WithSensor&lt;AlternatingToolLoopSensor&gt;()</c>.
/// </summary>
public sealed class AlternatingToolLoopSensor(int minCycles = 2) : ISensor
{
    public string Name => "alternating-tool-loop";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ToolCallStep current)
            return Task.FromResult(SensorResult.Pass);

        var window = 2 * minCycles;

        // Most-recent-first signatures: the pending call, then the preceding tool calls.
        var signatures = new List<string> { SignatureOf(current) };
        for (var i = state.Trajectory.Count - 1; i >= 0 && signatures.Count < window; i--)
        {
            if (state.Trajectory[i] is UserMessageStep)
                break;                         // a new user turn is a fresh intent — don't scan past it
            if (state.Trajectory[i] is ToolCallStep prior)
                signatures.Add(SignatureOf(prior));
        }

        if (signatures.Count < window)
            return Task.FromResult(SensorResult.Pass);

        var a = signatures[0];
        var b = signatures[1];
        if (a == b)
            return Task.FromResult(SensorResult.Pass); // identical-in-a-row is StuckDetector's job

        for (var i = 0; i < window; i++)
        {
            if (signatures[i] != (i % 2 == 0 ? a : b))
                return Task.FromResult(SensorResult.Pass);
        }

        return Task.FromResult(SensorResult.Intervene(
            $"Tool '{current.Call.ToolName}' is alternating with another call in an A-B-A-B loop " +
            $"({minCycles} cycles) without making progress. Break the cycle — try a different approach."));
    }

    private static string SignatureOf(ToolCallStep step) =>
        $"{step.Call.ToolName}::{step.Call.Arguments.GetRawText()}";
}
