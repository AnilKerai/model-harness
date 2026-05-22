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
        var result = await sensor.CheckAsync(hookPoint, state, triggeringStep, ct);
        return (sensor, result);
    }
}
