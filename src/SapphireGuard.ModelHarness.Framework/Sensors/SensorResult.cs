namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>Outcome of a sensor evaluation at a given hookpoint.</summary>
public sealed record SensorResult(bool IsIntervene, string? Reason)
{
    /// <summary>The sensor has no concerns at this hookpoint; the loop continues normally.</summary>
    public static SensorResult Pass { get; } = new(false, null);

    /// <summary>
    /// The sensor wants the loop to intervene. <paramref name="reason"/> is appended to
    /// the trajectory as a <c>SensorInterventionStep</c> and rendered as an assistant-role
    /// message on the next turn.
    /// </summary>
    public static SensorResult Intervene(string reason) => new(true, reason);
}
