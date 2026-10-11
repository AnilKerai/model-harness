# ADR-002 — Trajectory compaction is an incremental fold, executed in the guide

**Status:** Accepted. Port shape (`CompactAsync`/`CompactionRequest`/`CompactionResult`)
replaced `SummariseAsync` — a breaking change for `ICompactionStrategy` implementers.
Structural invariants and the fail-open contract were tightened in 2.4.0 and 2.5.0.

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

Four invariants a maintainer must preserve.

1. **System-role trajectory messages are folded into the single system message.** The
   rolling summary and the `[ORIGINAL GOAL]` anchor are emitted as `MessageRole.System`
   trajectory messages, and `DefaultContextBuilder` folds every System-role trajectory
   message into the one system message. Each adapter forwards only the first System message
   and drops the rest, so a summary left inline in the conversation would silently never
   reach the model.

2. **Everything rendered into the window is charged before trimming.** The anchor by its
   rendered length, the new summary via the prior summary's size as a stand-in. Omitting
   them overshoots `WindowTokens` by their size on every compacted turn. This invariant and
   (1) are a **pair**: the overshoot was inert while adapters dropped the extra System
   messages, and became real the moment (1) made them reach the model. `OriginalGoal(...)`
   is one definition shared by the charge and the render, so the two cannot diverge —
   counting one string and emitting another is how the overshoot arose.

3. **Eviction never removes the most recent group.** `ComputeTrimCount` is capped at
   `liveGroups.Count - 1`, with a fallback that renders the last group even when the
   watermark already covers everything. A fully-evicted turn would produce an empty
   `messages` array, which the Anthropic and Azure APIs reject with a 400. An over-budget
   message beats a hard failure.

4. **Fail-open, and never silently.** The fold call is wrapped (genuine cancellation
   excepted — see [ADR-001](001-collaborator-timeout-is-not-a-cancelled-run.md)) so a
   third-party strategy cannot take down the run. `AiCompactionStrategy` fails open the same
   way internally, leaving the watermark where it was so the slice is retried next turn. The
   reason always travels: `CompactionResult.Error` → `CompactionTrace.Error` →
   `error.type=compaction_error` on the OTel `compact_context` event, `error` on
   `ConsoleTracer`'s compaction line, a "Compaction fell back" line on the dashboard. A pass
   carrying an `Error` never reports `Folded`, even when the strategy handed the prior
   summary back.

A corollary of (4): any result that persists nothing — a view, or a fallback — describes
only the **newly evicted slice**, so the guide renders it *behind* the prior summary rather
than in place of it. The folded steps are no longer rendered anywhere else; the summary is
the only trace of them left.

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
