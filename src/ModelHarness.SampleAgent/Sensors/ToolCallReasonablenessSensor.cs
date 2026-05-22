using ModelHarness.Framework.Sensors;
using ModelHarness.Framework.State;

namespace ModelHarness.SampleAgent.Sensors;

/// <summary>
/// Demonstration sensor — always passes. Exists to show how a domain agent
/// wires a sensor into the loop without changing framework code.
/// </summary>
public sealed class ToolCallReasonablenessSensor : ISensor
{
    public string Name => "tool-call-reasonableness";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct) =>
        Task.FromResult(SensorResult.Pass);
}
