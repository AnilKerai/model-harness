using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Context;

/// <summary>
/// Result of a context build pass. Carries the assembled prompt, the guide-filtered tool list
/// (so the loop passes the correct subset to the model, keeping the prompt catalogue and the
/// model's tool definitions in sync), and any compaction the trajectory guide performed this
/// turn (<see langword="null"/> when none) so the loop can persist the rolling summary and spend.
/// </summary>
public sealed record ContextBuildResult(
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ITool> SelectedTools,
    CompactionResult? Compaction = null);
