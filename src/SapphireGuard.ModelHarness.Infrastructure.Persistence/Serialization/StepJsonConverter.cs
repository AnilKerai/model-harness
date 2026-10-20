using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence.Serialization;

/// <summary>
/// Polymorphic converter for the <see cref="Step"/> hierarchy. Writes a "$type"
/// discriminator so each subtype can be round-tripped without adding serialisation
/// attributes to the Framework domain model. <see cref="ModelCallStep.Prompt"/> is not persisted:
/// it is written as an empty list, so a checkpoint's size grows with the trajectory rather than
/// with every turn's whole context window. A checkpoint that still carries prompts loads as-is.
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
            nameof(UserMessageStep) => root.Deserialize<UserMessageStep>(options),
            nameof(ModelCallStep) => ReadModelCall(root, options),
            nameof(ToolCallStep) => root.Deserialize<ToolCallStep>(options),
            nameof(SensorInterventionStep) => root.Deserialize<SensorInterventionStep>(options),
            _ => throw new JsonException($"Unknown step type '{typeName}'")
        };
    }

    // A checkpoint with no Prompt property (or a null one) deserialises with a null Prompt, which the
    // non-nullable record member does not admit. Prompts are not persisted, so normalise it to empty.
    private static ModelCallStep? ReadModelCall(JsonElement element, JsonSerializerOptions options)
    {
        var step = element.Deserialize<ModelCallStep>(options);
        return step is { Prompt: null } ? step with { Prompt = [] } : step;
    }

    public override void Write(Utf8JsonWriter writer, Step value, JsonSerializerOptions options)
    {
        var node = value switch
        {
            UserMessageStep s => JsonSerializer.SerializeToNode(s, options),
            // Stripped here rather than at the checkpoint level so every copy is covered, including the
            // one a SensorInterventionStep carries as its TriggeringStep (the converter runs for it too).
            ModelCallStep s => JsonSerializer.SerializeToNode(s with { Prompt = [] }, options),
            ToolCallStep s => JsonSerializer.SerializeToNode(s, options),
            SensorInterventionStep s => JsonSerializer.SerializeToNode(s, options),
            _ => throw new JsonException($"Unknown step type '{value.GetType().Name}'")
        };

        var obj = node!.AsObject();
        obj[TypeProperty] = JsonValue.Create(value.GetType().Name);
        obj.WriteTo(writer, options);
    }
}
