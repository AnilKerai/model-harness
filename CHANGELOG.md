# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Releases are cut as annotated `vX.Y.Z` git
tags; CI publishes the seven NuGet packages from the tag (versioning is [MinVer](https://github.com/adamralph/minver)-driven,
so the tag *is* the version).

**Every breaking change is recorded here with a migration guide.** A breaking change means anything that
can stop a consumer compiling or silently change runtime behaviour they depend on — including changes to
extension-point interfaces (`IBudgetEnforcer`, `ITracer`, `ISensor`, `IGuide`, `IModelClient`, …), which are
public API even though most consumers only implement a few of them.

## [2.2.0] — 2026-07-18

Closes the medium-severity batch from the systematic audit. Minor rather than patch:
`AgentState.TotalSpend()` is a new public member. No breaking changes.

### Added

- `AgentState.TotalSpend()` — the run's cumulative `(Turns, Usage, Cost)` across model calls,
  delegated tool results, AI sensors, and compaction. `BudgetSnapshot.From` (and through it
  `DefaultBudgetEnforcer`) and `AgentTool` now both read it, so budget enforcement, per-turn
  telemetry, and delegated cost cannot drift apart.

### Fixed

- **Compaction overshot the context window by the size of the goal anchor.** The rolling summary and
  the `[ORIGINAL GOAL]` line are appended *after* the trim but ride in the same window, and neither
  was charged to the eviction budget — so every compacted turn rendered roughly `|TaskText|/4` tokens
  more than `CompactionOptions.WindowTokens` allowed. This was inert while the adapters dropped all
  but the first System message and became real in 2.0.0, which folded them into the system message so
  they actually reach the model. The anchor is now charged by its rendered length (from a single
  shared definition, so the charged text and the emitted text cannot diverge) and the prior summary's
  size stands in for the new one. (#16)

  **Behavioural change:** a compacted turn now keeps less history than before, by the size of the
  goal anchor — which is the point, but a run with a very long task text and a tight `WindowTokens`
  will notice. Raise `WindowTokens` if you were relying on the overshoot.

- **`AgentTool` under-charged the parent for a delegated sub-agent.** It aggregated the sub-agent's
  spend by walking `ModelCallStep` only, so a sub-agent that ran AI sensors, compacted, or delegated
  onward reported low — and the shortfall compounded with delegation depth. It now uses
  `AgentState.TotalSpend()`, the same accounting the budget enforcers and telemetry use. (#10)

  **Behavioural change:** parents are now charged the sub-agent's real spend, so a multi-agent run
  reaches `MaxCost`/`MaxTotalTokens` earlier than it used to. The old figure was wrong, not generous.

- **A single newline evaded `PromptInjectionSensor`'s override patterns.** The `.{0,N}` gaps between
  a trigger word and its object were matched without `RegexOptions.Singleline`, and `.` excludes `\n`
  by default — so `"Ignore all previous\ninstructions"` matched nothing, in a sensor whose stated
  premise is evasion resistance (`Normalize` strips Format characters, but newlines are Control).
  Singleline is now set once for every pattern rather than per-pattern, so a newly added gap cannot
  reinherit the hole. (#13)

  **Behavioural change:** detection is broader — gaps now span line breaks, so some multi-line content
  that previously slipped through will be flagged. The sensor is advisory at both hookpoints, so the
  cost of a false positive is a scepticism warning, not a blocked action.

- **`FileCheckpointStore` built filenames with the ambient culture.** The timestamp prefix is the sort
  key `LoadLatestAsync` relies on, and interpolated custom date formatting uses `CurrentCulture`: under
  a non-Gregorian calendar locale (th-TH, ar-SA, fa-IR) the rendered year differs — th-TH writes 2026
  as 2569 — which outsorts every Gregorian name and resumes from a stale checkpoint. Now formatted
  with `CultureInfo.InvariantCulture`. (#14)

  Existing checkpoints written under such a locale keep their old names and will still sort ahead;
  delete them (`ICheckpointStore.DeleteAsync`) if a task's directory has mixed-calendar names.

- **`TokensPerMinuteRateLimiter` reported a `RetryAfter` that was too short.** It timed the wait from
  the single oldest in-window call ageing out, but the window is a token *sum*: when evicting one
  entry doesn't bring the total under the ceiling, the wait ends early and the loop's re-check tail
  degenerates into ~1 s busy-polls, each carrying a checkpoint write. It now ages entries out
  oldest-first until the remainder clears the ceiling. Throttling was always safe — the gate is
  re-evaluated each pass, so no call ever slipped through — the harm was a wrong wait surfaced to
  telemetry and to the `MaxWallClock` projection, plus the wasted iterations. The sibling
  `CallsPerMinuteRateLimiter` was never affected: its count drops by exactly one per eviction. (#15)

- **Both rate limiters counted a call that had aged out of the window.** The 60-second window's lower
  bound was inclusive while `RetryAfter` targeted exactly that instant, so a correctly-sized wait
  never cleared the limit — it always cost one extra iteration before the 1 s fallback broke the tie.
  The bound is now exclusive. (#16)

- **Both rate limiters accepted a non-positive limit.** With a limit of `0` or less the pass-guard is
  false even for an empty window, and the `RetryAfter` path then indexed an empty list — a
  misconfiguration surfacing mid-run as an `IndexOutOfRangeException`. The constructors now throw
  `ArgumentOutOfRangeException` instead. (#16)

## [2.1.0] — 2026-07-18

Minor rather than patch: `AgentState.RunId` is a new public member. No breaking changes.

### Added

- `AgentState.RunId` — a stable run identifier derived from the first user message's step id, so it
  survives resume the same way `RunStartedAt` does.

### Fixed

- **`Checkpoint.RunId` changed across a resume.** The loop minted a fresh GUID on every `RunAsync`, so a
  HITL suspend/resume, checkpoint reload, or new chat turn stamped the same task's checkpoints with
  different run ids — contradicting the documented contract that "all checkpoints for the same task share
  this value". It is now derived from the trajectory. (#3)
- **A sensor's own timeout could kill the run.** `DefaultSensorRunner` re-threw *every*
  `OperationCanceledException`, but an AI-powered sensor whose HTTP call times out raises
  `TaskCanceledException` — an OCE — with the harness token untouched. That faulted the parallel batch and
  ended the run as "cancelled", breaking the fail-open guarantee. Only genuine harness cancellation
  propagates now; a sensor's own OCE fails open like any other fault. (#11)
- **Loop-detection sensors counted across user turns.** `StuckDetector`, `AlternatingToolLoopSensor`,
  `MonologueLoopSensor`, and `ToolErrorLoopSensor` scanned the trajectory backwards treating every
  non-matching step as transparent, so a `UserMessageStep` never reset the streak. In the shipped chat
  harness — which wires `StuckDetector` alongside `GetDateTimeTool`, whose empty-args schema gives every
  call an identical signature — a user asking the time in three separate turns had the third call
  **blocked** as a loop. All four now stop at the current user turn. (#12)

## [2.0.1] — 2026-07-18

### Fixed

- **`PromptInjectionSensor` no longer scans `skill_view` results.** A skill body is a stored procedure and
  therefore instruction-shaped by nature ("never call X before Y"), so the override patterns matched
  legitimate guidance routinely. `skill_view` joins `ask_human` in the sensor's trusted-tool allowlist.

  The exemption matters because `PostToolCall` is advisory and fires *after* the result is already
  committed — it cannot block anything, so a false positive only told the model to distrust content it had
  legitimately loaded.

  **Trust assumption worth knowing:** both allowlist entries assume that source is operator-authored. If
  your deployment lets untrusted parties write skills (agent learning) or answer `ask_human` (a relayed
  channel), that content is now unscanned — put a control at the write/ingress boundary instead, since a
  regex over instruction-shaped text is the wrong instrument there.

No public API change.

## [2.0.0] — 2026-07-18

Four HIGH-severity bugs found in a systematic audit, one of which required a breaking signature change.

### Breaking

- **`IBudgetEnforcer.Check` no longer takes the run's start time.**

  ```diff
  - BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt);
  + BudgetCheckResult Check(AgentState state, TimeSpan lookahead = default);
  ```

  The wall-clock anchor is now derived from the trajectory by the enforcer itself rather than passed in by
  the loop, which is what makes it survive a resume (see the `MaxWallClock` fix below). The new optional
  `lookahead` is added to the measured elapsed time before the wall-clock comparison, so the loop can ask
  *"would sleeping for this rate-limit backoff exhaust the budget?"* instead of doing its own wall-clock
  arithmetic with an anchor it cannot correctly choose.

  Both built-in enforcers (`DefaultBudgetEnforcer`, `TurnScopedBudgetEnforcer`) are already migrated. Only
  a **custom `IBudgetEnforcer` implementation** is affected.

#### Migration

If you implement `IBudgetEnforcer`, derive the anchor from the state and honour the lookahead:

```csharp
// Before
public BudgetCheckResult Check(AgentState state, DateTimeOffset startedAt)
{
    var elapsed = _time.GetUtcNow() - startedAt;
    ...
}

// After
public BudgetCheckResult Check(AgentState state, TimeSpan lookahead = default)
{
    // AgentState.RunStartedAt is the first user message's timestamp — the true task start,
    // preserved across checkpoint reload / HITL resume / a new chat turn.
    // For a per-turn allowance, anchor to the LAST UserMessageStep instead.
    var start = state.RunStartedAt ?? _time.GetUtcNow();
    var elapsed = _time.GetUtcNow() - start + lookahead;
    ...
}
```

If you only *call* `Check` (uncommon — the loop does), drop the second argument: `Check(state)`.

**Behavioural change even if you do not implement the interface:** `MaxWallClock` now accumulates across a
resume instead of restarting. A long-running task that suspends for human input and resumes will now
correctly exhaust its wall-clock budget where it previously received a fresh full allowance each time.
Runs that relied on that reset will now stop earlier — raise `MaxWallClock` if you were depending on it.

### Added

- `AgentState.RunStartedAt` — the run's true start (first `UserMessageStep` timestamp), derived from the
  trajectory so it survives resume.

### Fixed

- **The system prompt lost its harness and ReAct priming on every turn.** `SystemPromptGuide` is registered
  by `WithSystemPrompt(...)` from inside the configure callback, so it runs *after* the default pipeline —
  and it assigned `draft.SystemPrompt`, wiping the harness-observation priming and ReAct framing that
  `HarnessInstructionsGuide` and `ReActGuide` had appended with `+=`. The assembled system prompt was
  reduced to just the caller's string. It now prepends, which is correct regardless of registration order.
- **Compaction silently deleted history instead of summarising it.** The rolling summary and the
  `[ORIGINAL GOAL]` anchor were emitted as `MessageRole.System` trajectory messages, but every model adapter
  forwards only the *first* System message and drops the rest — so neither ever reached the model, and an
  `AiCompactionStrategy` fold paid for a summary that was thrown away. `DefaultContextBuilder` now folds
  every System-role trajectory message into the single system message adapters forward.
- **`MaxWallClock` reset on every resume.** The loop captured the start time per `RunAsync` invocation, so
  a HITL suspend/resume, checkpoint reload, or new chat turn restarted the wall-clock budget from zero.
  It is now anchored to the trajectory: the first user message for `DefaultBudgetEnforcer`, the latest user
  message for `TurnScopedBudgetEnforcer` (whose per-turn allowance is meant to reset).
- **A fully evicted trajectory produced an empty `messages` array**, which the Anthropic and Azure APIs
  reject with a 400, failing the run. Eviction now never removes the most recent group.

## [1.2.1] — 2026-07-17

### Fixed

- A request could end on an assistant turn when a sensor intervention was the final step, which newer
  Claude models (Opus 4.8 / Sonnet 5 / the 4.6+ line) reject with a 400 — the retired prefill behaviour.
  `HeadEvictionTrajectoryGuide` now appends a trailing user turn after a final intervention note, so the
  note keeps its self-consistency framing while a user turn carries the API-required final role.

## [1.2.0] — 2026-07-16

### Added

- `PromptInjectionSensor` expanded coverage: 10 new pattern families, HTML-entity and Unicode
  normalization anti-evasion, invisible-text detection, and scanning of pinned content.

## [1.1.1] — 2026-07-15

### Fixed

- Turn numbering restarted at zero across a resume. `HarnessLoop` seeded its turn counter to 0 on every
  `RunAsync`, so a resumed run (HITL suspend/resume, checkpoint reload, or new chat turn) restarted the
  loop-emitted turn numbering while the guide-derived index kept counting. The counter is now seeded from
  the restored trajectory's `ModelCallStep` count.

## [1.1.0] — 2026-07-14

No public API removed.

### Added

- **`WithStructuredOutput<T>()`** — constrains a run's final answer to a type. One
  `StructuredOutputContract<T>` (schema + `JsonSerializerOptions` + `TryBind`) is shared by a guide that
  states the JSON Schema in the system prompt every turn, a `PreReturn` sensor that binds the final answer,
  and the caller that reads it — so none can drift. A non-binding answer is challenged with the binder's own
  error and given a fresh turn with tools suppressed, not thrown. The schema is a guide's system section
  rather than a trajectory message, so it is unreachable by head eviction by construction.
- **`ModelResponse.InputTokensTowardRateLimit`** — the share of a call's input that counts against the
  provider's input-tokens-per-minute limit. Nullable: an adapter that has not declared its provider's
  accounting reports null, and consumers count the full prompt.

### Fixed

- `TokensPerMinuteRateLimiter` counted cache reads toward the token limit. Anthropic excludes
  `cache_read_input_tokens` from ITPM, so the limiter throttled a well-cached agent several times too
  early — and got worse the better the cache performed. It now counts what the provider counts.

  **Behavioural change:** if you relied on the old over-throttling as an implicit safety margin, this will
  send more traffic; set the ceiling from your ITPM limit.

## [1.0.0] — 2026-07-08

First stable release. A .NET agent framework built as *model + harness*: the loop, guides, sensors, budget,
compaction, and checkpoint/resume. Adapters for Anthropic, Azure OpenAI / AI Foundry, and Ollama.
OpenTelemetry `gen_ai.*` tracing. Published as seven NuGet packages.

## [0.1.0] — 2026-06-05

Initial packaging release: multi-targeted `net8.0` + `net10.0`, MIT licence, SourceLink, and shared package
metadata across the seven publishable projects.

[2.2.0]: https://github.com/AnilKerai/model-harness/releases/tag/v2.2.0
[2.1.0]: https://github.com/AnilKerai/model-harness/releases/tag/v2.1.0
[2.0.1]: https://github.com/AnilKerai/model-harness/releases/tag/v2.0.1
[2.0.0]: https://github.com/AnilKerai/model-harness/releases/tag/v2.0.0
[1.2.1]: https://github.com/AnilKerai/model-harness/releases/tag/v1.2.1
[1.2.0]: https://github.com/AnilKerai/model-harness/releases/tag/v1.2.0
[1.1.1]: https://github.com/AnilKerai/model-harness/releases/tag/v1.1.1
[1.1.0]: https://github.com/AnilKerai/model-harness/releases/tag/v1.1.0
[1.0.0]: https://github.com/AnilKerai/model-harness/releases/tag/v1.0.0
[0.1.0]: https://github.com/AnilKerai/model-harness/releases/tag/v0.1.0
