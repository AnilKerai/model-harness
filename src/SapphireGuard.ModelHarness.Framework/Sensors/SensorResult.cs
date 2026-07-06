using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>Outcome of a sensor evaluation at a given hookpoint.</summary>
public sealed record SensorResult(bool IsIntervene, string? Reason, bool SuppressTools = false, Usage? Usage = null, decimal? Cost = null, bool IsError = false)
{
    /// <summary>The sensor has no concerns at this hookpoint; the loop continues normally.</summary>
    public static SensorResult Pass { get; } = new(false, null);

    /// <summary>
    /// The sensor has no concerns at this hookpoint but incurred model usage doing so.
    /// Usage is propagated to the run budget. An optional <paramref name="reason"/> is recorded
    /// for tracing/diagnostics only — it does not trigger an intervention.
    /// </summary>
    public static SensorResult PassWithUsage(Usage usage, decimal cost, string? reason = null) => new(false, reason, Usage: usage, Cost: cost);

    /// <summary>
    /// The sensor wants the loop to intervene. <paramref name="reason"/> is appended to
    /// the trajectory as a <c>SensorInterventionStep</c> and rendered as an assistant-role
    /// message on the next turn.
    /// </summary>
    public static SensorResult Intervene(string reason) => new(true, reason);

    /// <summary>
    /// The sensor wants the loop to intervene and incurred model usage doing so.
    /// <paramref name="reason"/> is appended to the trajectory and usage is propagated to the run budget.
    /// </summary>
    public static SensorResult InterveneWithUsage(string reason, Usage usage, decimal cost) => new(true, reason, Usage: usage, Cost: cost);

    /// <summary>
    /// The sensor wants the loop to intervene and suppress tools on the next model call,
    /// so the model must respond in text without making further tool calls.
    /// Use when the data-gathering phase is complete and only a textual correction is needed.
    /// </summary>
    public static SensorResult InterveneWithToolSuppression(string reason) => new(true, reason, SuppressTools: true);

    /// <summary>
    /// The sensor wants the loop to intervene with tool suppression and incurred model usage doing so.
    /// Usage is propagated to the run budget.
    /// </summary>
    public static SensorResult InterveneWithToolSuppressionAndUsage(string reason, Usage usage, decimal cost) => new(true, reason, SuppressTools: true, Usage: usage, Cost: cost);

    /// <summary>
    /// The sensor threw and was skipped (fail-open): it neither intervenes nor blocks, so the loop
    /// proceeds and the model keeps its turn, but the failure is recorded in telemetry (as an error,
    /// not a pass) rather than silently swallowed. Produced by the sensor runner when a sensor's
    /// <c>CheckAsync</c> throws. A sensor that must fail <em>closed</em> (e.g. a hard security gate)
    /// should catch its own exception and return an <see cref="Intervene(string)"/> instead.
    /// </summary>
    public static SensorResult Failed(string reason) => new(false, reason, IsError: true);
}
