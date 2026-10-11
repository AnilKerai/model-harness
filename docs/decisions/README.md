# Architecture Decision Records

Why the harness is built the way it is. `CLAUDE.md` and `docs/EXTENDING.md` state the
**invariants** — what must stay true and how to work with it. These records hold the
**reasoning**: the problem that forced each decision, what was rejected, and what breaks
if the decision is reverted.

Read one when you are about to change the thing it describes. You should not need any of
them to use the framework.

| ADR | Decision | Touches |
|---|---|---|
| [001](001-collaborator-timeout-is-not-a-cancelled-run.md) | A collaborator's own timeout is not a cancelled run | `HarnessLoop`, `DefaultSensorRunner`, `DefaultGuideRunner`, `HeadEvictionTrajectoryGuide`, `ResilientTool`, `TaintTrackingSensor` |
| [002](002-incremental-fold-compaction.md) | Trajectory compaction is an incremental fold, executed in the guide | `ICompactionStrategy`, `HeadEvictionTrajectoryGuide`, `CompactionOptions`, `AgentState.RollingSummary` |
| [003](003-structured-output-as-a-prereturn-validator.md) | Structured output is a `PreReturn` validator, not a generator constraint | `StructuredOutputContract<T>`, `StructuredOutputGuide<T>`, `StructuredOutputSensor<T>` |

## Writing a new one

Number sequentially. Four headings: **Context** (the problem, with the failure that
forced it), **Decision** (what the code does now), **Consequences** (what a maintainer
must keep true), **Alternatives rejected** (and the specific reason each lost). State
facts; the record is not an argument that needs winning.

One rule across the docs: **every fact lives in exactly one file.** `CHANGELOG.md` owns
*what changed and when*. These records own *why*. `CLAUDE.md` owns *what must stay true*.
`docs/EXTENDING.md` owns *how do I*. Nothing is repeated between them — a pointer is
cheaper than a copy, and a copy is what goes stale.
