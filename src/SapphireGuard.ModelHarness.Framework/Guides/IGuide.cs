using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Shapes what the model perceives on the next turn. Guides run sequentially
/// in registration order before each model call, each contributing to a shared
/// <see cref="ContextDraft"/> that the context builder then assembles into a prompt.
///
/// The guide/sensor split is intentional: sensors <em>observe and intervene</em>;
/// guides <em>shape perception</em>. Sensor intervention records feed back through
/// the guide pipeline so their rendering is also a guide concern.
/// </summary>
public interface IGuide
{
    /// <summary>Unique identifier used in traces and diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Contributes to the context that will be sent to the model on the next turn.
    /// Write into <paramref name="draft"/> fields — system prompt, trajectory messages,
    /// memory snippets, available tools, or additional system sections.
    /// </summary>
    Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct);
}
