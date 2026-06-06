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
        new HashSet<HookPoint> { HookPoint.PreReturn };

    public async Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        var formatResult = await outputFormat.CheckAsync(hookPoint, state, triggeringStep, ct);
        if (formatResult.IsIntervene)
            return formatResult;

        return await evidenceGrounding.CheckAsync(hookPoint, state, triggeringStep, ct);
    }
}
