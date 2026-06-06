using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace GettingStarted.Sensors;

/// <summary>
/// Composite PreReturn sensor that gates evidence grounding behind format validation.
/// OutputFormatSensor runs first; if the format is wrong the agent is sent back to fix
/// structure without burning an LLM call on grounding a malformed output.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class VerificationReportSensor(
    OutputFormatSensor outputFormat,
    EvidenceGroundingSensor evidenceGrounding) : ISensor
{
    public string Name => "verification-report";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PostModelCall, HookPoint.PreReturn };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct) =>
        hookPoint switch
        {
            // Format check at PostModelCall: bad response is suppressed from context so the model
            // cannot anchor on a partial response and restarts data-gathering instead of fixing format.
            HookPoint.PostModelCall => outputFormat.CheckAsync(hookPoint, state, triggeringStep, ct),
            // Evidence grounding at PreReturn: model sees its (correctly formatted) answer and can
            // self-correct specific verdicts without re-running the full workflow.
            HookPoint.PreReturn     => evidenceGrounding.CheckAsync(hookPoint, state, triggeringStep, ct),
            _                       => Task.FromResult(SensorResult.Pass)
        };
}
