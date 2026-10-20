# ADR-002 — Trajectory compaction is an incremental fold, executed in the guide

**Status:** Accepted. The port shape (`CompactAsync`/`CompactionRequest`/`CompactionResult`)
replaced `SummariseAsync` in 1.0.0 — a breaking change for `ICompactionStrategy` implementers.
The structural invariants were each forced by a later bug: 2.0.0 (System-message folding),
2.2.0 (charging before trimming), 2.5.0 (fail-open reporting and rendering behind the prior
summary).

## Context

`HeadEvictionTrajectoryGuide` drops the oldest trajectory steps when the rendered context
approaches the window. Something has to stand in their place, and the first implementation
was a **view**: re-summarise the entire evicted head from scratch every turn. Two problems.

1. **Cost grew with the run.** The evicted head only ever gets longer, so each turn's
   summarisation call was larger than the last. On a long-horizon run the compaction spend
   outgrew the saving it existed to produce — and it was not attributed to any budget, so
   nothing stopped it.
2. **The eviction trigger was the same field as the run's token cap.** `Budget.MaxContextTokens`
   meant both "how big is one window" (what eviction needs) and "how many tokens may this
   run spend in total" (what the enforcer needs). Cumulative usage is always ≥ one window,
   so the enforcer hard-stopped compacted runs at roughly one window — before compaction
   could help at all.

A third question had no obvious answer: **where** does the fold execute? The natural
instinct is a step in `HarnessLoop`, since the result must land on `AgentState` and guides
are otherwise pure `ContextDraft` shapers.

Two bugs after the fold shipped explain why two of its invariants are a pair. In 2.0.0 the
rolling summary and `[ORIGINAL GOAL]` anchor turned out never to reach the model at all: they
were emitted as extra System messages, and every adapter forwards only the first, so a fold paid
for a summary that was thrown away. Folding them into the single system message fixed that — and
immediately exposed the second bug, fixed in 2.2.0: neither had ever been charged to the eviction
budget, so every compacted turn now overshot `WindowTokens` by their size. The overshoot had been
inert only because the text it counted was being discarded. Counting one string and emitting
another is the general failure, which is why `OriginalGoal(...)` is now a single definition shared
by the charge and the render. The chat-mode `[CURRENT REQUEST]` note (the latest user message, rendered
as a System message when its group is evicted or behind the watermark) follows the same rule through
`CurrentRequest(...)`.

## Decision

**Fold, not view.** `ICompactionStrategy.CompactAsync(CompactionRequest, ct)` returns a
`CompactionResult` carrying `InjectedText`, a nullable `UpdatedSummary`, and `Usage`/`Cost`.

- `UpdatedSummary = null` — a stateless **view**. `NullCompactionStrategy` (the default,
  a bare omission note) and any strategy that reconsiders the head each turn.
- `UpdatedSummary` non-null — an incremental **fold**. `AiCompactionStrategy` summarises
  only the newly evicted slice *on top of* the prior summary, so per-turn cost stays flat
  however long the run gets.

`AgentState.RollingSummary` holds the summary text plus a `FoldedStepCount` watermark. The
guide skips that many head groups, so a folded step is never re-summarised. The trajectory
remains the append-only source of truth; the summary is derived state beside it.
`CompactionRequest` carries the **typed** `EvictedSteps` rather than pre-rendered text, so a
strategy can compact structurally.

**The fold executes in the guide, with one explicit write-back channel.** The eviction
decision needs the post-pipeline token budget, which only exists inside
`HeadEvictionTrajectoryGuide` — it runs last precisely so it can measure every prior
contribution. So the guide computes the fold and hands the result out:

```
HeadEvictionTrajectoryGuide → ContextDraft.Compaction → ContextBuildResult
                            → HarnessLoop.CallModelAsync → next AgentState
```

The loop commits `RollingSummary` (a fold persists it; a view returns null and the prior is
kept) and accumulates `CompactionUsage`/`CompactionCost`. This is the **only** place a
guide's output flows back to `AgentState`, and it is loop-owned.

**Trigger split from the run cap.** `CompactionOptions.WindowTokens` is the per-turn window
the guide keeps under; `Budget.MaxTotalTokens` is the cumulative run cap the enforcer checks.
Both opt-in methods (`WithAiCompaction(model, options)`, `WithCompactionStrategy<T>(options)`)
**require** `CompactionOptions`, so a strategy can never be wired without stating its trigger.

**Spend is budgeted.** `CompactionUsage`/`CompactionCost` on `AgentState` mirror
`SensorUsage`/`SensorCost` and are counted by both budget enforcers.

## Consequences

The invariants this decision imposes are stated in `CLAUDE.md` (Guide pattern → compaction) and
not repeated here. What the decision costs:

- **The guide is no longer a pure shaper.** Compaction is the one channel by which a guide's
  output reaches `AgentState`. It is loop-owned and explicit, but it is a precedent — the next
  guide that wants to persist something should be refused, not given a second channel.
- **A fold is lossy, and permanently so.** Steps behind the watermark survive only as prose. A
  poor summary cannot be recovered within the run: the watermark never moves back, and the
  original steps are no longer rendered.
- **State carries a second, derived field.** `RollingSummary` is checkpointed and rehydrated
  verbatim on resume — trusted, not recomputed — so a summary written by one strategy is folded
  onward by whatever strategy is wired after a resume.
- **Strategy authors carry a subtle contract.** A fold's `InjectedText` is the complete
  replacement summary; a view's describes only the newly evicted slice. Mixing them up
  duplicates or drops history, and nothing type-checks it.
- **Compaction spends the run's budget.** A long run can exhaust `MaxCost` on summarisation
  rather than on the task.

## Alternatives rejected

- **A compaction step in `HarnessLoop`.** The first design. It cannot see the post-pipeline
  token budget, which is the input the eviction decision is made from.
- **Defaulting the AI fold on in `AddStandardModelHarness`.** Compaction trades fidelity for
  bounded context, so it only earns its keep on long-horizon runs. Most tasks are bounded
  and should size `WindowTokens` to their natural peak — below the window the guide does
  nothing and the run pays zero cost and zero lossiness. Defaulting it on would tax every
  bounded run for a scaling problem it does not have. The 100k default keeps compaction
  dormant unless a run genuinely overflows.
- **Pre-rendered text in `CompactionRequest`.** Passing typed steps costs nothing and lets a
  strategy compact structurally (truncate bulky tool results, drop redundant steps) instead
  of only prose-summarising.

Deferred, none blocked by this: deterministic structured clearing as a cheap pre-fold layer,
an anti-thrash guard, a real `compact_context` span with the fold's model call as a nested
`chat` child, and a zero-arg `WithAiCompaction()` reusing the registered `IModelClient`.
