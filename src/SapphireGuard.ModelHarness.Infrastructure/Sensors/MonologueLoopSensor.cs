using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Opt-in. Flags when the model emits essentially the same text response with no tool calls
/// <paramref name="repeatThreshold"/> times in a row (default 3) — the agent is "talking to
/// itself" instead of acting, usually because it is ignoring sensor feedback and re-stating the
/// same answer. Hooks <see cref="HookPoint.PostModelCall"/>, so the repeated response is
/// suppressed and the model gets a fresh turn. Not registered by default; opt in with
/// <c>builder.WithSensor&lt;MonologueLoopSensor&gt;()</c>.
/// </summary>
public sealed class MonologueLoopSensor(int repeatThreshold = 3) : ISensor
{
    public string Name => "monologue-loop";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PostModelCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ModelCallStep current || current.Response.ToolCalls.Count > 0)
            return Task.FromResult(SensorResult.Pass);

        var key = Normalise(current.Response.Text);
        if (key.Length == 0)
            return Task.FromResult(SensorResult.Pass);

        var consecutive = 1;
        for (var i = state.Trajectory.Count - 1; i >= 0; i--)
        {
            if (state.Trajectory[i] is UserMessageStep)
                break;                         // a new user turn is a fresh intent — don't scan past it
            if (state.Trajectory[i] is not ModelCallStep prior || ReferenceEquals(prior, current))
                continue;
            if (prior.Response.ToolCalls.Count > 0 || Normalise(prior.Response.Text) != key)
                break;
            if (++consecutive >= repeatThreshold)
                return Task.FromResult(SensorResult.Intervene(
                    $"You have repeated essentially the same response {consecutive} times without taking action. " +
                    "Stop restating it — either take a concrete next step with a tool, or finalise only if you have new information."));
        }

        return Task.FromResult(SensorResult.Pass);
    }

    // ponytail: collapse whitespace + lowercase so trivial reformatting isn't seen as a new answer.
    private static string Normalise(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
