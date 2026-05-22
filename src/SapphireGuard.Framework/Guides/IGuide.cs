using SapphireGuard.Framework.State;

namespace SapphireGuard.Framework.Guides;

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
    string Name { get; }

    Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct);
}
