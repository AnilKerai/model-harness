using System.ComponentModel;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Output;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Output;

public sealed class StructuredOutputContractTests
{
    [Description("A triaged support ticket.")]
    private sealed record Triage(
        [property: Description("The queue this ticket belongs in.")] string Category,
        int Priority);

    private static readonly StructuredOutputContract<Triage> Sut = new();

    // ── Binding ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryBind_WellFormedAnswer_Binds()
    {
        var ok = Sut.TryBind("""{"category":"billing","priority":2}""", out var value, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(new Triage("billing", 2), value);
    }

    // The load-bearing test. System.Text.Json treats constructor parameters as optional by default, so
    // without RespectRequiredConstructorParameters this binds to Triage { Category = null, Priority = 0 }
    // and the sensor rubber-stamps an empty answer.
    [Fact]
    public void TryBind_BraceGroupInThePreamble_StillFindsTheRealPayload()
    {
        // "First balanced value" is not good enough: a brace group in the prose *before* the payload
        // wins the scan, fails to bind, and costs a needless repair turn. Each balanced value is tried
        // in order until one deserializes.
        var ok = Sut.TryBind("""Result {see below}: {"category":"billing","priority":2}""", out var value, out _);

        Assert.True(ok);
        Assert.Equal(new Triage("billing", 2), value);
    }

    [Fact]
    public void TryBind_NestedPayloadAfterProse_PrefersTheOutermostMatch()
    {
        // The scan still yields outermost-first, so a legitimately nested object binds whole rather
        // than the inner fragment being picked off.
        var ok = Sut.TryBind("""Here you go: {"category":"billing","priority":2}""", out var value, out _);

        Assert.True(ok);
        Assert.Equal("billing", value!.Category);
    }

    [Fact]
    public void TryBind_EmptyObject_Fails()
    {
        var ok = Sut.TryBind("{}", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryBind_MissingRequiredField_FailsAndNamesTheField()
    {
        var ok = Sut.TryBind("""{"category":"billing"}""", out _, out var error);

        Assert.False(ok);
        Assert.Contains("riority", error);   // the binder's own message names the missing member
    }

    [Fact]
    public void TryBind_NullText_Fails()
    {
        Assert.False(Sut.TryBind(null, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryBind_ProseWithNoJson_Fails()
    {
        Assert.False(Sut.TryBind("I was unable to categorise this ticket.", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryBind_MalformedJson_FailsWithBinderError()
    {
        Assert.False(Sut.TryBind("""{"category":"billing",""", out _, out var error));
        Assert.NotNull(error);
    }

    // ── Leniency: what weaker models actually emit ────────────────────────────

    [Fact]
    public void TryBind_JsonCodeFence_Binds()
    {
        var answer = """
                     ```json
                     {"category":"billing","priority":2}
                     ```
                     """;

        Assert.True(Sut.TryBind(answer, out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }

    [Fact]
    public void TryBind_BareCodeFence_Binds()
    {
        var answer = "```\n{\"category\":\"billing\",\"priority\":2}\n```";

        Assert.True(Sut.TryBind(answer, out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }

    [Fact]
    public void TryBind_ProseAroundPayload_Binds()
    {
        var answer = """Here is the triage: {"category":"billing","priority":2}. Hope that helps!""";

        Assert.True(Sut.TryBind(answer, out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }

    // The prose scanner must not treat a brace inside a string literal as the end of the payload.
    [Fact]
    public void TryBind_ProseAroundPayloadWithBraceInsideString_Binds()
    {
        var answer = """Result: {"category":"bil}ling","priority":2} — done.""";

        Assert.True(Sut.TryBind(answer, out var value, out _));
        Assert.Equal(new Triage("bil}ling", 2), value);
    }

    [Fact]
    public void TryBind_EscapedQuoteInsideString_Binds()
    {
        var answer = """Result: {"category":"say \"hi\"","priority":2}""";

        Assert.True(Sut.TryBind(answer, out var value, out _));
        Assert.Equal(new Triage("""say "hi" """.TrimEnd(), 2), value);
    }

    [Fact]
    public void TryBind_MismatchedCasing_Binds()
    {
        Assert.True(Sut.TryBind("""{"Category":"billing","Priority":2}""", out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }

    [Fact]
    public void TryBind_NumberAsString_Binds()
    {
        Assert.True(Sut.TryBind("""{"category":"billing","priority":"2"}""", out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }

    // The text channel carries any JSON value, so a non-object root needs no wrapping.
    [Fact]
    public void TryBind_ArrayRoot_Binds()
    {
        var contract = new StructuredOutputContract<List<int>>();

        Assert.True(contract.TryBind("[1, 2, 3]", out var value, out _));
        Assert.Equal([1, 2, 3], value);
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    [Fact]
    public void Schema_SurfacesDescriptionAttributes()
    {
        Assert.Contains("A triaged support ticket.", Sut.Schema);
        Assert.Contains("The queue this ticket belongs in.", Sut.Schema);
    }

    [Fact]
    public void Schema_RootIsNonNullableObject()
    {
        using var doc = JsonDocument.Parse(Sut.Schema);

        // Null-oblivious roots otherwise export as ["object", "null"], which reads to the model
        // as "null is an acceptable answer".
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void SystemSection_StatesTheSchemaAndScopesItToTheFinalAnswer()
    {
        Assert.Contains(Sut.Schema, Sut.SystemSection);
        Assert.Contains("final answer", Sut.SystemSection);
        Assert.Contains("Intermediate turns are unconstrained", Sut.SystemSection);
    }

    [Fact]
    public void Options_SuppliedByCaller_AreUsedForSchemaAndBinding()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            RespectRequiredConstructorParameters = true
        };
        var contract = new StructuredOutputContract<Triage>(options);

        // PascalCase naming (JsonSerializerOptions.Default) flows into both the schema and the binder.
        Assert.Contains("\"Category\"", contract.Schema);
        Assert.True(contract.TryBind("""{"Category":"billing","Priority":2}""", out var value, out _));
        Assert.Equal(new Triage("billing", 2), value);
    }
}
