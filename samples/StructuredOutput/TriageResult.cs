using System.ComponentModel;

namespace SapphireGuard.ModelHarness.Samples.StructuredOutput;

/// <summary>
/// The contract the agent's final answer must satisfy. <c>[Description]</c> flows into the JSON Schema
/// the model is shown, so the fields document themselves rather than arriving as a bare shape.
/// </summary>
[Description("A triaged support ticket.")]
public sealed record TriageResult(
    [property: Description("The queue this ticket belongs in: billing, technical, or account.")]
    string Category,
    [property: Description("Urgency from 1 (lowest) to 5 (highest).")]
    int Priority,
    [property: Description("One sentence a human operator can act on without reading the ticket.")]
    string Summary);
