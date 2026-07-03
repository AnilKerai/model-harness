namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// A labelled block of reference content pinned into the persistent context region. Rendered as a
/// non-evictable system section every turn, so it is never subject to trajectory eviction or
/// compaction — the fix for foundational content (a loaded procedure, an output contract, a spec)
/// that would otherwise be lost when its early trajectory step is evicted.
/// </summary>
/// <remarks>
/// A tool contributes one via <see cref="ToolResult.Pins"/> (e.g. <c>skill_view</c> pinning a loaded
/// skill body); the loop commits it to <see cref="State.AgentState.Pins"/>, replacing any existing pin
/// with the same <see cref="Label"/>. Analogous to a Letta/MemGPT core-memory block, but written on
/// load rather than agent-editable.
/// </remarks>
/// <param name="Label">Stable identifier for the pin; re-pinning the same label replaces its content.</param>
/// <param name="Content">The reference content to keep verbatim in context.</param>
public sealed record PinnedNote(string Label, string Content);
