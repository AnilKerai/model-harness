using AgentHarness.Framework.State;

namespace AgentHarness.Framework.Sensors;

/// <summary>Fan-out runner that evaluates every sensor registered at a hookpoint.</summary>
public interface ISensorRunner
{
    Task<IReadOnlyList<(ISensor Sensor, SensorResult Result)>> RunAsync(
        HookPoint hookPoint,
        AgentState state,
        Step? triggeringStep,
        CancellationToken ct);
}
