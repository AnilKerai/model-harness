namespace SapphireGuard.ModelHarness.Framework.Sensors;

/// <summary>Outcome of a sensor evaluation at a given hookpoint.</summary>
public sealed record SensorResult(bool IsIntervene, string? Reason)
{
    public static SensorResult Pass { get; } = new(false, null);

    public static SensorResult Intervene(string reason) => new(true, reason);
}
