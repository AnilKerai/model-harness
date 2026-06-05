using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;

/// <summary>
/// Polymorphic converter for the <see cref="Step"/> hierarchy. Writes a "$type"
/// discriminator so each subtype can be round-tripped without adding serialisation
/// attributes to the Framework domain model.
/// </summary>
public sealed class StepJsonConverter : JsonConverter<Step>
{
    private const string TypeProperty = "$type";

    public override Step? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty(TypeProperty, out var typeEl))
            throw new JsonException("Missing $type discriminator on Step");

        var typeName = typeEl.GetString()
            ?? throw new JsonException("Missing $type discriminator on Step");

        return typeName switch
        {
            nameof(ModelCallStep) => root.Deserialize<ModelCallStep>(options),
            nameof(ToolCallStep) => root.Deserialize<ToolCallStep>(options),
            nameof(SensorInterventionStep) => root.Deserialize<SensorInterventionStep>(options),
            _ => throw new JsonException($"Unknown step type '{typeName}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, Step value, JsonSerializerOptions options)
    {
        var node = value switch
        {
            ModelCallStep s => JsonSerializer.SerializeToNode(s, options),
            ToolCallStep s => JsonSerializer.SerializeToNode(s, options),
            SensorInterventionStep s => JsonSerializer.SerializeToNode(s, options),
            _ => throw new JsonException($"Unknown step type '{value.GetType().Name}'")
        };

        var obj = node!.AsObject();
        obj[TypeProperty] = JsonValue.Create(value.GetType().Name);
        obj.WriteTo(writer, options);
    }
}
