using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Persistence;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;

[ExcludeFromCodeCoverage]
public static class CheckpointSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new StepJsonConverter() }
    };

    public static string Serialize(Checkpoint checkpoint) =>
        JsonSerializer.Serialize(checkpoint, Options);

    public static Checkpoint? Deserialize(string json) =>
        JsonSerializer.Deserialize<Checkpoint>(json, Options);
}
