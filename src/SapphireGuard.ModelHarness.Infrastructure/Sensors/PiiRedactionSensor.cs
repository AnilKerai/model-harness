using System.Text.RegularExpressions;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Scans model output at PostModelCall for common PII patterns (email, phone,
/// credit card, UK NI, US SSN) and blocks the response before it leaves the
/// harness. Acts as a last-resort compliance boundary — the model should never
/// emit PII, but this catches cases where it does despite instructions.
/// </summary>
public sealed class PiiRedactionSensor : ISensor
{
    public string Name => "pii-redaction";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PostModelCall };

    private static readonly (string Label, Regex Pattern)[] Patterns =
    [
        ("email",       new Regex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",                   RegexOptions.Compiled)),
        ("phone",       new Regex(@"(?<!\d)(?=[+\d])\+?(?!(?:19|20)\d{2}[-\s])(?=(?:[^\d]*\d){9})[\d \t\-()]{9,17}", RegexOptions.Compiled)),
        ("credit-card", new Regex(@"\b(?:\d[ \-]?){13,16}\b",                                             RegexOptions.Compiled)),
        ("uk-ni",       new Regex(@"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",                                 RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("us-ssn",      new Regex(@"\b\d{3}-\d{2}-\d{4}\b",                                               RegexOptions.Compiled)),
    ];

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ModelCallStep modelStep)
            return Task.FromResult(SensorResult.Pass);

        var text = modelStep.Response.Text;
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(SensorResult.Pass);

        foreach (var (label, pattern) in Patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
                continue;

            var priorViolations = state.Trajectory
                .OfType<SensorInterventionStep>()
                .Count(s => s.SensorName == Name);

            var matched = match.Value.Length > 40 ? match.Value[..40] + "…" : match.Value;
            var reason = priorViolations == 0
                ? $"Response contains possible PII ({label}: \"{matched}\"). Restate your answer without including any personal data."
                : $"Response contains possible PII ({label}: \"{matched}\"). This is violation {priorViolations + 1} — you have already been blocked {priorViolations} time(s). You must not include any personal data whatsoever.";

            return Task.FromResult(SensorResult.Intervene(reason));
        }

        return Task.FromResult(SensorResult.Pass);
    }
}
