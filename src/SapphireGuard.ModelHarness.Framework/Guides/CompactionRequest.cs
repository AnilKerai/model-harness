using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Everything a <see cref="ICompactionStrategy"/> needs to compact one slice of trajectory.
/// A framework extension point: strategies receive the <em>typed</em> steps being evicted (not
/// pre-rendered text) plus the full run context, so they can implement prose summarisation,
/// structured clearing, goal-aware compression, or anything else. Passed as a record so new inputs
/// can be added later without breaking existing implementations.
/// </summary>
public sealed record CompactionRequest
{
    /// <summary>Full immutable run context — task text, budget, the complete trajectory, and metadata.</summary>
    public required AgentState State { get; init; }

    /// <summary>
    /// The trajectory steps being evicted from the live context this turn, in order. Inspect the
    /// concrete step types (<see cref="ModelCallStep"/>, <see cref="ToolCallStep"/>,
    /// <see cref="SensorInterventionStep"/>) to compact structurally rather than as opaque text.
    /// </summary>
    public required IReadOnlyList<Step> EvictedSteps { get; init; }

    /// <summary>
    /// The summary accumulated so far, to fold the newly evicted steps onto; <see langword="null"/>
    /// on the first compaction. Equal to <see cref="State"/>'s <c>RollingSummary</c>, surfaced here
    /// as the primary fold input. A stateless (view) strategy ignores it.
    /// </summary>
    public required RollingSummary? PriorSummary { get; init; }

    /// <summary>Approximate token headroom the injected text should fit under (roughly characters ÷ 4).</summary>
    public required int RemainingTokenBudget { get; init; }
}
