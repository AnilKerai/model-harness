namespace SapphireGuard.Framework.Sensors;

/// <summary>Outcome of a sensor evaluation at a given hookpoint.</summary>
public sealed record SensorResult(bool IsBlock, string? Reason)
{
    public static SensorResult Pass { get; } = new(false, null);

    public static SensorResult Block(string reason) => new(true, reason);
}
