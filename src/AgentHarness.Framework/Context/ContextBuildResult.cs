using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;

namespace AgentHarness.Framework.Context;

/// <summary>
/// Result of a context build pass. Carries both the assembled prompt and the
/// guide-filtered tool list so the loop can pass the correct subset to the
/// model — keeping the prompt catalogue and the model's tool definitions in sync.
/// </summary>
public sealed record ContextBuildResult(
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ITool> SelectedTools);
