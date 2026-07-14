using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace SapphireGuard.ModelHarness.Framework.Output;

/// <summary>
/// The output contract for a structured run: the JSON Schema derived from <typeparamref name="T"/>, the
/// system-prompt section that states it to the model, and the rules that decide whether a final answer
/// satisfies it.
/// <para>
/// Registered once by <c>WithStructuredOutput&lt;T&gt;</c> and shared by three collaborators — the guide
/// that states the contract, the sensor that enforces it on the final answer, and the caller that binds
/// the result via <see cref="TryBind"/> — so none of them can drift on the schema or the binding rules.
/// </para>
/// </summary>
/// <typeparam name="T">The type the run's final answer must bind to.</typeparam>
public sealed class StructuredOutputContract<T>
{
    // System.Text.Json treats every constructor parameter as optional by default, so
    // Deserialize<Person>("{}") yields Person { Name = null, Age = 0 } without throwing — validation
    // would rubber-stamp an empty object, which is the most common way a model gets this wrong.
    // Web is the base for its leniency where leniency is safe: case-insensitive property matching and
    // numbers-from-strings both rescue answers that are correct in substance but sloppy in form.
    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerOptions.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    private static readonly JsonSchemaExporterOptions SchemaOptions = new()
    {
        // Without this the root type exports as ["object", "null"], which reads to the model as
        // "null is an acceptable answer".
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddDescriptions
    };

    private static readonly JsonSerializerOptions SchemaWriteOptions = new() { WriteIndented = true };

    /// <summary>Creates the contract for <typeparamref name="T"/>.</summary>
    /// <param name="options">
    /// Serializer options used both to derive the schema and to bind the answer. Defaults to
    /// <see cref="JsonSerializerOptions.Web"/> with <c>RespectRequiredConstructorParameters</c> and
    /// <c>RespectNullableAnnotations</c> enabled — supply your own only if you need different
    /// naming or converters, and keep those two flags on or an incomplete answer will bind.
    /// </param>
    public StructuredOutputContract(JsonSerializerOptions? options = null)
    {
        Options = options ?? DefaultOptions;
        Schema = Options.GetJsonSchemaAsNode(typeof(T), SchemaOptions).ToJsonString(SchemaWriteOptions);
        SystemSection =
            $"""
             # Output contract

             Your final answer — the message you send once you are finished and are making no further
             tool calls — must be a single JSON value matching the schema below, and nothing else: no
             prose before or after it, and no markdown code fences.

             Intermediate turns are unconstrained. This applies only to the final answer.

             ## Schema
             {Schema}
             """;
    }

    /// <summary>Serializer options used to derive <see cref="Schema"/> and to bind answers against it.</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>The JSON Schema for <typeparamref name="T"/>, as shown to the model.</summary>
    public string Schema { get; }

    /// <summary>The rendered system-prompt section stating the contract. Emitted every turn by the guide.</summary>
    public string SystemSection { get; }

    /// <summary>
    /// Attempts to bind <paramref name="text"/> — typically <c>AgentOutcome.FinalAnswer</c> — to
    /// <typeparamref name="T"/>. Tolerates a markdown code fence and surrounding prose, which weaker
    /// models emit despite the contract asking for neither.
    /// </summary>
    /// <param name="text">The model's final answer.</param>
    /// <param name="value">The bound value when this returns <see langword="true"/>.</param>
    /// <param name="error">
    /// When this returns <see langword="false"/>, the binder's own message — which names the missing or
    /// malformed field. Fed straight back to the model by the sensor, so it can self-correct.
    /// </param>
    public bool TryBind(string? text, out T? value, out string? error)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The response was empty; expected a JSON value matching the output contract.";
            return false;
        }

        var candidate = StripCodeFence(text.Trim());
        if (TryDeserialize(candidate, out value, out error))
            return true;

        // Prose around the payload ("Here is the result: { ... }"). Retry on the first balanced value,
        // but keep the primary error — it describes what the model actually produced.
        if (ExtractFirstJsonValue(candidate) is { } embedded && TryDeserialize(embedded, out value, out _))
        {
            error = null;
            return true;
        }

        return false;
    }

    private bool TryDeserialize(string json, out T? value, out string? error)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is null)
            {
                error = "The response deserialized to null; expected a JSON value matching the output contract.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            value = default;
            error = ex.Message;
            return false;
        }
    }

    // ```json … ``` and bare ``` … ``` fences. The contract asks for none; models add them anyway.
    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);

        return firstNewline > 0 && closingFence > firstNewline
            ? text[(firstNewline + 1)..closingFence].Trim()
            : text;
    }

    // First balanced JSON object or array, ignoring brackets inside string literals. Null when none.
    private static string? ExtractFirstJsonValue(string text)
    {
        var start = text.AsSpan().IndexOfAny('{', '[');
        if (start < 0)
            return null;

        var open = text[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (c == open)
                depth++;
            else if (c == close && --depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    // Surfaces [Description] on the type and its members as JSON Schema `description` keywords, so the
    // schema documents itself to the model rather than being a bare shape.
    private static JsonNode AddDescriptions(JsonSchemaExporterContext context, JsonNode schema)
    {
        ICustomAttributeProvider? provider = context.PropertyInfo is not null
            ? context.PropertyInfo.AttributeProvider
            : context.TypeInfo.Type;

        var description = provider?
            .GetCustomAttributes(inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()?
            .Description;

        if (description is not null && schema is JsonObject obj && !obj.ContainsKey("description"))
            obj.Insert(0, "description", description);

        return schema;
    }
}
