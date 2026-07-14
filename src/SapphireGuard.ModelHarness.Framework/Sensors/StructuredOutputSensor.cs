using SapphireGuard.ModelHarness.Framework.Output;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>
/// Enforces the run's output contract on the final answer.
/// <para>
/// Runs at <see cref="HookPoint.PreReturn"/>, which the loop reaches only on a turn that made no tool
/// calls — so this is the final turn, identified with certainty and without constraining any
/// intermediate reasoning turn.
/// </para>
/// <para>
/// An answer that does not bind to <typeparamref name="T"/> is challenged, not thrown: the binder's own
/// error — which names the missing or malformed field — goes back to the model as a
/// <see cref="SensorInterventionStep"/>, and it gets a fresh turn to correct itself with tools suppressed,
/// so the repair turn can only reformat. The loop's intervention cap bounds the retries, ending in
/// <see cref="AgentStatus.PartialResult"/> rather than a failure.
/// </para>
/// </summary>
/// <typeparam name="T">The type the run's final answer must bind to.</typeparam>
public sealed class StructuredOutputSensor<T>(StructuredOutputContract<T> contract) : ISensor
{
    public string Name => "structured-output";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreReturn };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ModelCallStep step)
            return Task.FromResult(SensorResult.Pass);

        if (contract.TryBind(step.Response.Text, out _, out var error))
            return Task.FromResult(SensorResult.Pass);

        return Task.FromResult(SensorResult.InterveneWithToolSuppression(
            $"Your final answer did not satisfy the output contract: {error} " +
            "Reply with a single JSON value matching the schema in the output contract, and nothing else."));
    }
}
