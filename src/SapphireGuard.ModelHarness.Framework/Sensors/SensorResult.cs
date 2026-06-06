namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>Outcome of a sensor evaluation at a given hookpoint.</summary>
public sealed record SensorResult(bool IsIntervene, string? Reason, bool SuppressTools = false)
{
    /// <summary>The sensor has no concerns at this hookpoint; the loop continues normally.</summary>
    public static SensorResult Pass { get; } = new(false, null);

    /// <summary>
    /// The sensor wants the loop to intervene. <paramref name="reason"/> is appended to
    /// the trajectory as a <c>SensorInterventionStep</c> and rendered as an assistant-role
    /// message on the next turn.
    /// </summary>
    public static SensorResult Intervene(string reason) => new(true, reason);

    /// <summary>
    /// The sensor wants the loop to intervene and suppress tools on the next model call,
    /// so the model must respond in text without making further tool calls.
    /// Use when the data-gathering phase is complete and only a textual correction is needed.
    /// </summary>
    public static SensorResult InterveneWithToolSuppression(string reason) => new(true, reason, SuppressTools: true);
}
