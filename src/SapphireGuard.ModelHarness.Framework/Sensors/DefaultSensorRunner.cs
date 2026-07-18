using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>
/// Runs every sensor whose <see cref="ISensor.HookPoints"/> includes the current
/// hookpoint, in parallel, and returns all results so the loop can decide how
/// to react.
/// </summary>
public sealed class DefaultSensorRunner(IEnumerable<ISensor> sensors) : ISensorRunner
{
    private readonly IReadOnlyList<ISensor> _sensors = [.. sensors];

    public async Task<IReadOnlyList<(ISensor Sensor, SensorResult Result)>> RunAsync(
        HookPoint hookPoint,
        AgentState state,
        Step? triggeringStep,
        CancellationToken ct)
    {
        var active = _sensors.Where(s => s.HookPoints.Contains(hookPoint)).ToArray();
        if (active.Length == 0)
        {
            return [];
        }

        var tasks = active.Select(s => RunOne(s, hookPoint, state, triggeringStep, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<(ISensor Sensor, SensorResult Result)> RunOne(
        ISensor sensor, HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        try
        {
            var result = await sensor.CheckAsync(hookPoint, state, triggeringStep, ct);
            return (sensor, result);
        }
        // Only genuine harness cancellation propagates. A sensor's *own* OperationCanceledException —
        // an HttpClient timeout surfaces as TaskCanceledException even though our token is untouched —
        // must fall through to the fail-open catch below, or one slow AI sensor faults the whole
        // Task.WhenAll batch and ends the run as "cancelled".
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail open: one misbehaving sensor must not fault the whole batch (Task.WhenAll) and
            // take down the run. The failure is surfaced as an error result for telemetry, and the
            // loop treats it as a non-intervention so the model keeps its turn.
            return (sensor, SensorResult.Failed($"{sensor.Name} threw {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
