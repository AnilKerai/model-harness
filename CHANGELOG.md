# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Releases are cut as annotated `vX.Y.Z` git
tags; CI publishes the eight NuGet packages from the tag (versioning is [MinVer](https://github.com/adamralph/minver)-driven,
so the tag *is* the version).

**Every breaking change is recorded here with a migration guide.** A breaking change means anything that
can stop a consumer compiling or silently change runtime behaviour they depend on — including changes to
extension-point interfaces (`IBudgetEnforcer`, `ITracer`, `ISensor`, `IGuide`, `IModelClient`, …), which are
public API even though most consumers only implement a few of them.

## [Unreleased]

### Fixed

- **The forced-finalisation instruction now reaches the model.** When the budget is exhausted, or the
  sensor-intervention cap (3 consecutive rejections) is hit, `HarnessLoop` makes one tool-free call with
  "Budget is exhausted. Produce your best final answer now … Do not request more tools." It used to add that
  as a *second* System message, and every adapter (`ClaudeModelClient`, `AzureOpenAIModelClient`,
  `OllamaModelClient`) forwards only the first System message — so the model was never told to answer. The
  instruction is now appended to the leading System message (`"\n\n" + instruction`); if a custom
  `IContextBuilder` returns no leading System message, one holding the instruction is prepended. The prompt
  recorded on `ModelCallStep.Prompt` and passed to `ITracer.BeginModelCall` is the folded list that was sent.
  *Behavioural change, no migration needed:* the finalisation prompt now has one System message instead of
  two. A custom `IModelClient`, tracer or test that asserted a trailing System message on the finalisation
  call should read the instruction from `messages[0]` instead.

## [3.1.0] — 2026-10-11

A minor release with two threads: Langfuse-ready traces, using only the standard OpenTelemetry GenAI
attributes — the library still has no OpenTelemetry SDK or vendor dependency, so the same spans read correctly
in any GenAI-aware backend, and the new `samples/Langfuse` exports a two-turn chat to Langfuse over OTLP — and
Claude pricing brought up to date. Nothing is removed and nothing stops compiling. The behavioural changes are
corrections, each marked below with what to re-check: `ModelResponse.Cost` for Claude models now matches the
first-party price list, the content-capture JSON follows the GenAI schema (opt-in), and a double-registered
tracer emits one span tree instead of two.

### Added

- **`ITracer.LogOutcome(AgentOutcome)`** — called once per run, immediately before `Complete`, on every exit
  path (`Done`, `Failed`, `PartialResult`, `AwaitingHuman`), with the outcome the caller is about to receive. A
  default-interface no-op, so existing tracers compile unchanged; `CompositeTracer` forwards it.
- **`OpenTelemetryTracer` records the run's input and output on the `invoke_agent` root** — with
  `enableSensitiveData`, `gen_ai.input.messages` is the user message that started the run (in a chat, the
  current turn, not the opener `TaskText` holds) and `gen_ai.output.messages` is the final or partial answer.
  Backends that show a trace's input/output (Langfuse's trace table, evaluators, dataset runs) read it from the
  root, which had neither.
- **`gen_ai.conversation.id` on every span** — the task id, which stays the same across chat turns and HITL
  resumes, so a backend groups a conversation's runs (one trace per `RunAsync`) into a session. A sub-agent run
  nested under a parent's tool span inherits the parent's id. Not content, so it is emitted regardless of
  `enableSensitiveData`.
- **`samples/Langfuse`** — OTLP export to Langfuse (endpoint, Basic auth, `x-langfuse-ingestion-version: 4`,
  `deployment.environment.name`, flush on exit). Keys come from `LANGFUSE_PUBLIC_KEY` / `LANGFUSE_SECRET_KEY` /
  `LANGFUSE_BASE_URL` or `appsettings.local.json`. See [EXTENDING.md](docs/EXTENDING.md#exporting-to-langfuse).

### Changed

- **`gen_ai.input.messages` / `gen_ai.output.messages` follow the GenAI messages JSON schema.** They were a
  `{role, content}` projection of the harness's internal messages; they are now role + typed `parts` as the
  semantic conventions require. Tool calls and results become `tool_call` / `tool_call_response` parts instead
  of `[tool_call …]` text, adjacent messages with the same role merge (as the adapters send them), and
  `gen_ai.output.messages` is an array carrying a `finish_reason`. Only emitted with `enableSensitiveData`.

  **Behavioural change** — if you parse these attributes yourself, read the new shape:

  ```jsonc
  // Before — gen_ai.output.messages
  {"role":"assistant","content":"Let me check.","tool_calls":[{"id":"c1","name":"calculator","arguments":{"op":"mul"}}]}

  // After
  [{"role":"assistant","parts":[{"type":"text","content":"Let me check."},
    {"type":"tool_call","id":"c1","name":"calculator","arguments":{"op":"mul"}}],"finish_reason":"tool_call"}]
  ```

  `gen_ai.response.finish_reasons` is unchanged.

### Fixed

- **`ClaudeModelClient` priced Claude from a stale three-family table.** It matched only `haiku` / `opus` /
  everything-else, at May 2026 rates: every Haiku at $0.80 / $4, every Opus at $15 / $75, and everything
  else (Sonnet, Fable, unknown ids) at $3 / $15 per million input / output tokens, with cache reads at 0.1×
  input for all. Current first-party prices differ on almost every model, and a family cannot tell Opus 4.x
  from Opus 5.5, Sonnet 4.6 from Sonnet 5.x, or Haiku 4.5 from Haiku 5.5. Pricing now lives in an internal
  `ClaudePricing` table matched by the longest model-id prefix (so dated ids such as
  `claude-haiku-4-5-20251001` resolve), checked against the pricing page on 2026-10-11:

  | Model | Input | Output | Cache read |
  |---|---:|---:|---:|
  | Fable 5.1 / Mythos 5.1 | $10 | $50 | $0.25 (0.025×) |
  | Fable 5 / Mythos 5 | $10 | $50 | $1.00 |
  | Opus 5.5 | $4 | $20 | $0.20 (0.05×) |
  | Opus 5, 4.8, 4.7, 4.6, 4.5 | $5 | $25 | $0.50 |
  | Sonnet 5.5 | $2 | $10 | $0.10 (0.05×) |
  | Sonnet 5 | $2 | $10 | $0.20 |
  | Sonnet 4.6, 4.5 | $3 | $15 | $0.30 |
  | Haiku 5.5, prompt up to 100K tokens | $0.10 | $0.50 | $0.01 |
  | Haiku 5.5, prompt over 100K tokens | $0.50 | $2.50 | $0.05 |
  | Haiku 4.5 | $1 | $5 | $0.10 |

  5-minute cache writes stay at 1.25× input. Haiku 5.5 is priced per request by prompt length: a prompt over
  100,000 tokens — counting cache reads and writes — moves the whole request, output included, to the higher
  row. An id missing from the table is now priced at the top tier ($10 / $50) rather than at Sonnet's rate, so
  a stale table over-reports and stops a `MaxCost` budget early instead of letting a run overspend unnoticed.

  **Behavioural change:** `ModelResponse.Cost` changes for every Claude model, and with it everything summed
  from it — `AgentState.TotalSpend()`, `Budget.MaxCost` enforcement, sub-agent spend rolled up by
  `AgentTool`, the `harness.cost` telemetry and the live dashboard. A Haiku 4.5 run now reports 25% more
  than before and a Fable 5.x run 3⅓× as much, so they reach a `MaxCost` cap sooner; Opus 4.x reports a
  third, Sonnet 5.x two thirds and Haiku 5.5 an eighth (five eighths on long prompts) of before, so they reach
  it later. No code change is needed. Re-check any `MaxCost` set against the old figures, and drop any
  downstream repricing written to work around this (for example re-deriving Haiku 4.5 cost from token usage).
  For Haiku 4.5, `harness.cost` now matches the figure Langfuse computes from the same tokens.

- **`WithOtelTracer(...)` on a standard harness emitted every span twice.** `AddStandardModelHarness`,
  `AddStandardChatHarness` and `AgentFactory.AddStandardAgent` already add an `OpenTelemetryTracer`, and
  `WithOtelTracer` — the documented way to turn on content capture or set an agent name — added a second, so
  each run produced two nested `invoke_agent` trees and a backend summed every token and cost figure twice. A
  harness now has one `OpenTelemetryTracer`; calling `WithOtelTracer` again reconfigures it (the last call's
  settings win).

  **Behavioural change:** if you call `WithOtelTracer` on a standard harness, you now get one span tree with
  the settings you passed. Before, you got two: one with defaults (no content, no agent name) and yours nested
  inside it.

## [3.0.0] — 2026-10-11

A response that did not end normally is no longer reported as a finished answer. `HarnessLoop` never
read `StopReason`, and every adapter mapped a reason it did not recognise to `EndTurn`, so a truncated,
refused or filtered response came back as `Done` — the cause was visible only by digging the last
`ModelCallStep.Response.StopReason` out of the trajectory. Behavioural, and `StopReason` gains members;
every break below carries its migration. The reasoning is in
[ADR-004](docs/decisions/004-response-is-final-only-when-it-ended-normally.md).

### Breaking

- **Only a response that ended normally is `Done`.** `EndTurn`, `StopSequence` and `ToolUse` are the
  normal endings. Every other `StopReason` now returns `PartialResult`, keeping the response text (if
  any) as `FinalAnswer`, with a `FailureReason` naming the cause and the remedy:

  | Stop reason (provider value) | 2.5.0 | Now — `FailureReason` says |
  |---|---|---|
  | `MaxTokens` (Anthropic `max_tokens`, Azure `length`, Ollama `length`) | `Done`, answer ending mid-sentence | output-token limit — raise the adapter's `MaxOutputTokens`, or narrow the task |
  | `ContextWindowExceeded` (Anthropic `model_context_window_exceeded`) | `Done` | context window — shrink the prompt: lower `CompactionOptions.WindowTokens`, or narrow the task |
  | `Refusal` (Anthropic `refusal`) | `Done` | the model declined — retry on a fallback model, or rephrase |
  | `ContentFilter` (Azure `content_filter`) | `Done` | the provider's content filter withheld or cut the response |
  | `Other` (anything unrecognised — e.g. Anthropic `pause_turn`, an empty Ollama stream) | `Done` | unrecognised stop reason |

  The check runs after the `PostModelCall` sensors, so content gates still apply, and before the
  `PreReturn` sensors, so a `WithStructuredOutput<T>` validator never tries to repair a remnant. It
  spends no extra model call. Budget exhaustion is unchanged — it still gets one tool-free finalisation
  call — but when that finalisation response is itself cut short, its reason now says so.

  ```csharp
  // Before — a truncated or refused answer was indistinguishable from a complete one
  if (outcome.Status == AgentStatus.Done)
      Publish(outcome.FinalAnswer);        // could be a mid-sentence fragment, or a refusal

  // After — the partial text is still available, and the reason says why it is partial
  if (outcome.Status == AgentStatus.Done)
      Publish(outcome.FinalAnswer);
  else if (outcome.Status == AgentStatus.PartialResult)
      Review(outcome.FinalAnswer, outcome.FailureReason);
  ```

- **Tool calls from a cut-short response are not dispatched.** In 2.5.0 a response truncated mid-call
  ran the tool on whatever arguments had arrived: the Anthropic adapter passed the partial input through,
  and the Azure adapter threw parsing the truncated JSON, so the run ended `Failed` with a parser message.
  The run now returns `PartialResult` before any dispatch. On Azure, unparseable arguments on a cut-short
  response are kept as their raw text: not thrown, so the truncation is what gets reported, and not
  dropped, so the call stays visible in the trajectory and traces. Malformed arguments on a response that
  ended normally still throw. If a tool call is being cut off, raise `MaxOutputTokens`.

- **`StopReason` gained `Refusal`, `ContentFilter` and `ContextWindowExceeded`, and every member now has
  an explicit value.** The existing values are unchanged (`EndTurn = 0` … `Other = 4`), so a checkpoint
  written by 2.5.0 reads back the same; the new members are appended, and members only ever will be. A
  `switch` over `StopReason` with no discard arm no longer covers every value (CS8509 at compile time,
  `SwitchExpressionException` at run time). One with a discard sends the new values to it — check that
  arm does not treat them as success:

  ```csharp
  // Before — covered all five 2.5.0 members
  string Label(StopReason r) => r switch
  {
      StopReason.EndTurn or StopReason.StopSequence => "ok",
      StopReason.ToolUse => "tool",
      StopReason.MaxTokens => "truncated",
      StopReason.Other => "unknown",
  };

  // After — name the new members, and make the discard mean "not ok"
  string Label(StopReason r) => r switch
  {
      StopReason.EndTurn or StopReason.StopSequence => "ok",
      StopReason.ToolUse => "tool",
      StopReason.MaxTokens or StopReason.ContextWindowExceeded => "truncated",
      StopReason.Refusal or StopReason.ContentFilter => "declined",
      _ => "unknown",
  };
  ```

  **Custom `IModelClient` authors:** map a provider reason you do not recognise to `StopReason.Other`,
  never `EndTurn` — `EndTurn` is how a response claims to be a finished answer, and the loop believes it.
  See `docs/EXTENDING.md`.

- **`AgentTool` no longer hands a parent agent an unfinished sub-agent run as a clean result.** In 2.5.0
  it returned `FinalAnswer ?? FailureReason` and set `IsError` only for `Failed`: a sub-agent that ran out
  of budget handed back its best-effort answer as a normal result with the reason dropped, and one that
  stopped to ask a human returned `"(no result)"` as a success. Now:

  | Sub-agent outcome | Parent receives |
  |---|---|
  | `Done` | the answer — unchanged |
  | `PartialResult` with an answer | the answer, then `[INCOMPLETE — sub-agent '<name>' stopped early: <reason>]`; not an error, since a partial answer is still usable |
  | `PartialResult` with no answer | an error naming the reason |
  | `AwaitingHuman` | an error carrying the sub-agent's question, which cannot be relayed through a delegated call |
  | `Failed` | an error carrying the reason — unchanged |

  A parent prompt or sensor that matches on the sub-agent's result text should allow for the suffix.

### Changed

- **Every adapter maps the stop reasons it knows, and an unrecognised one to `Other`, never `EndTurn`.**
  - **Anthropic:** `refusal` → `Refusal`, `model_context_window_exceeded` → `ContextWindowExceeded`,
    anything else (including `pause_turn`, which only follows server-tool loops) → `Other`. The mapping
    moved into an internal, unit-tested `ClaudeWireFormat`.
  - **Azure OpenAI:** `content_filter` → `ContentFilter`; an unrecognised or missing finish reason → `Other`.
  - **Ollama:** now reads `done_reason`. `length` → `MaxTokens` — the adapter never reported truncation
    before, although it sends `MaxOutputTokens` as `num_predict`. `stop` → `EndTurn`, or `ToolUse` when
    the response carries tool calls; any other value → `Other`. A response with no `done_reason` — every
    server before v0.1.35 — keeps the previous inference from the tool calls; ADR-004 says why it is not
    treated as abnormal.
- **OpenTelemetry `gen_ai.response.finish_reasons`** gains `content_filter`, `refusal` and
  `model_context_window_exceeded`.

### Fixed

- **`AiCompactionStrategy` persisted a truncated or refused summary as the run's permanent record.** It
  rejected only an empty response, so a summary cut off at the output-token limit — or a refusal — became
  the `RollingSummary`, the watermark advanced past the steps it claimed to cover, and those steps were
  never rendered again. A response that did not end normally now fails open like any other compaction
  fault: the prior summary is kept, the slice is retried next turn, the call's spend is still charged,
  and `CompactionResult.Error` names the stop reason.
- `AgentOutcome.FailureReason`'s XML doc said it was set only for `Failed`; it is set for `PartialResult`
  too, and always has been.
- `CompactionRequest.EvictedSteps`' XML doc, and this file's 1.0.0 migration for `CompactAsync`, omitted
  `UserMessageStep` from the step types a strategy receives. It is the first step evicted from every
  trajectory, so a strategy that switched over only the listed types silently dropped user turns.

## [2.5.0] — 2026-10-10

A reliability release, started from a repo audit. The common thread: a component's own timeout — an
`HttpClient` timeout surfaces as `TaskCanceledException` with the harness token untouched — was being
read as "the run was cancelled", so runs ended, a security gate failed open, and circuit breakers
never opened. Alongside it, the Azure adapter was dropping assistant text from history, and a failing
AI sensor or compaction fold was invisible in telemetry. No API removed; one additive property pair.
Every entry that changes behaviour you can observe is marked.

### Fixed

- **The Azure OpenAI adapter dropped assistant text from the conversation history.** When it batched a
  run of assistant turns into one `AssistantChatMessage` it kept only the *last* text, and discarded
  the text entirely whenever the run included a tool call. Two consequences: a `PreReturn` challenge
  (`CriticSensor`, the `WithStructuredOutput<T>()` repair loop) renders as `[answer, harness
  observation]`, so the observation overwrote the very answer the model was being asked to correct;
  and a `PostToolCall` advisory (a `PromptInjectionSensor` or `TaintTrackingSensor` warning) vanished
  from history as soon as another tool call followed it. The run is still sent as a single assistant
  message — providers differ in how they treat back-to-back assistant messages — but every text in it
  is now kept, joined with a blank line, and carried on the same message as the tool call.

  **Behavioural change:** Azure requests now include assistant text that was previously missing, so
  prompts are slightly larger and the model sees its own earlier text and every harness observation.
  The Anthropic and Ollama adapters were not affected.

- **A guide's or compaction strategy's own timeout ended the run as "cancelled".** An `HttpClient`
  timeout surfaces as `TaskCanceledException` — an `OperationCanceledException` — even though the
  harness token was never cancelled. `DefaultGuideRunner` and `HeadEvictionTrajectoryGuide` rethrew
  every `OperationCanceledException`, so a slow memory store, skill store, or third-party
  `ICompactionStrategy` took the whole run down instead of degrading the turn — the case their
  fail-open handling exists for. Both now let only *genuine* cancellation (`ct.IsCancellationRequested`)
  propagate, matching what `DefaultSensorRunner` already did for sensors.

  **Behavioural change:** a run that previously returned `Failed` ("cancelled") on such a timeout now
  continues: the guide's contribution is skipped and logged via `LogGuideError`, or the evicted slice
  falls back to an omission note.

- **A model client's own timeout ended the run as "cancelled", with no detail on the `chat` span.**
  The same `TaskCanceledException` confusion, at the model call. `HarnessLoop` skipped
  `IModelCallScope.Fail` for every `OperationCanceledException` and reported every one as a
  cancellation, so a provider request that timed out came back as `Failed` with `FailureReason`
  `"cancelled"` — though nothing had cancelled the run — and the span said only that the call did not
  complete. The loop now treats an `OperationCanceledException` as cancellation only when its own
  token is cancelled. Anything else is a failure like any other: the `chat` span gets the exception
  (`exception.type`/`message`/`stacktrace`), and the outcome carries its message. The same holds for
  any other collaborator the loop awaits directly — a checkpoint store or rate limiter that times out
  is no longer reported as a cancellation either.

  **Behavioural change:** `AgentOutcome.FailureReason` for such a run is now the exception's message
  (for an `HttpClient` timeout, "The request was canceled due to the configured HttpClient.Timeout of
  … seconds elapsing.") instead of `"cancelled"`, and the reason passed to `ITracer.Complete` changes
  with it. `Status` is still `Failed`. `"cancelled"` now means exactly one thing — the
  `CancellationToken` you passed to `RunAsync` was cancelled — so code that compares against it to
  skip error reporting or a retry needs no edit, but will start seeing these timeouts as the failures
  they are.

  A custom `ITracer` is affected in one way: `IModelCallScope.Fail` is now called with an
  `OperationCanceledException` for a client-side timeout. It was never passed one before.

- **`TaintTrackingSensor` failed open when a custom `ITrustPolicy` timed out.** Its `PreToolCall`
  gate catches a throwing policy so that it fails *closed*, but the catch let every
  `OperationCanceledException` through. A policy that blocks on I/O with a timeout raises
  `TaskCanceledException`; that escaped the sensor, `DefaultSensorRunner` failed it open like any other
  sensor fault, and the privileged tool call went ahead with tainted content in context. The sensor
  now lets only genuine harness cancellation propagate — which ends the run before the tool is
  dispatched — and blocks on everything else. The built-in `TrustPolicy` does no I/O and was never
  affected.

  **Behavioural change:** with a custom `ITrustPolicy` that can time out, a privileged call that
  previously ran is now blocked, and the model is told the trust policy could not be evaluated.

- **The circuit breakers never opened on a dependency that timed out.** The same
  `TaskCanceledException` confusion, in `Infrastructure.Resilience`. Both default breakers —
  `ResilientModelClientDecorator`'s and `ResilientTool`'s — left every `OperationCanceledException`
  out of what they count, and Polly records an outcome its predicate rejects as a *success*. So a
  provider or tool backend that hangs until the client's own timeout elapses — the outage a breaker
  exists for — never opened the circuit, and a timed-out probe in the half-open state closed it. Both
  now exclude only the *caller* cancelling the call; the callee's own timeout counts as a failure.

  **Behavioural change:** after a run of such timeouts (at least half of four or more calls in 30
  seconds) the next calls fail fast with `BrokenCircuitException` for 15 seconds instead of each
  waiting out the timeout. In a harness run, an open model breaker ends the run as `Failed` with that
  exception's message; an open tool breaker is a recoverable `IsError` tool result. `ResilientTool`'s
  **retry** is unchanged: it still does not retry a tool's own timeout, because the call may already
  have run its side effect. A pipeline you pass in yourself is untouched.

- **Every wrapped model client shared one circuit breaker, and every resilient tool another.** The
  default pipelines were `static`, so their failure history was process-wide. An outage at one
  provider fast-failed calls to every other client wrapped with `WithResilientModel` — a sub-agent's,
  or a second provider's. For tools it was worse: one failed invocation is one attempt plus three
  retries, which is exactly the breaker's minimum throughput, so a single failing tool call blocked
  *every* `WithResilientTool` tool for 15 seconds. Each decorator and each tool built with the
  default policy now gets its own breaker.

  **Behavioural change:** nothing changes for a harness wired through `WithResilientModel` /
  `WithResilientTool` beyond the isolation — both register singletons, so each breaker still
  accumulates history for the life of the container. If you construct the wrappers yourself **per
  call or per request**, each new instance now starts with a closed breaker that never accumulates;
  build one pipeline and pass it in to keep the history, which is also how to share a breaker
  deliberately:

  ```csharp
  // Before — every instance fed the same process-wide breaker
  IModelClient Create() => new ResilientModelClientDecorator(new ClaudeModelClient(options));

  // After — own the pipeline, and with it the breaker's lifetime and scope
  private static readonly ResiliencePipeline<ModelResponse> Breaker =
      new ResiliencePipelineBuilder<ModelResponse>()
          .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ModelResponse> { /* … */ })
          .Build();

  IModelClient Create() => new ResilientModelClientDecorator(new ClaudeModelClient(options), Breaker);
  ```

- **A compaction result that persisted nothing dropped the rolling summary from context.** The guide
  rendered a strategy's `InjectedText` *in place of* the prior summary. That is right for a fold, which
  returns the whole updated summary — but a result with `UpdatedSummary = null` describes only the
  newly evicted slice, and the steps behind the fold watermark are no longer rendered, so the model saw
  neither them nor their summary. Two ways to get there: a custom fold strategy that throws (the guide
  falls back to a bare omission note), and a checkpoint written under a fold strategy then resumed with
  the default view strategy wired. The guide now keeps the prior summary in front of any such result,
  as `AiCompactionStrategy`'s own fail-open path always did.

  **Behavioural change — affects a custom `ICompactionStrategy`:** for *every* result with
  `UpdatedSummary = null` over a state that already carries a rolling summary, the prompt now renders
  that summary ahead of your `InjectedText`. A strategy that was already repeating the prior summary
  in such a result would now show it twice — return only the slice:

  ```csharp
  // Before — repeated the prior summary itself while persisting nothing
  return new CompactionResult { InjectedText = $"{request.PriorSummary?.Text}\n{note}" };

  // After — describe only the newly evicted slice; the harness keeps the summary in front
  return new CompactionResult { InjectedText = note };
  ```

  A fold (`UpdatedSummary` non-null) is unaffected: its `InjectedText` still replaces the prior summary.

- **An empty compaction summary was billed but not charged to the run.** When the compaction model
  returned nothing usable, `AiCompactionStrategy` failed open without the call's usage and cost, so
  that spend never reached `AgentState.CompactionUsage` / `CompactionCost` — and the slice is retried
  every turn.

  **Behavioural change:** that spend now counts toward `MaxCost` and `MaxTotalTokens`.

### Changed

- **`CriticSensor` no longer swallows a failed model call.** It caught every exception and returned
  `SensorResult.Pass`, so a permanently broken critic (a bad key, a retired model) was
  indistinguishable from one that approved every answer. It now lets the exception propagate.
  `DefaultSensorRunner` already fails open on a throwing sensor, so the answer is still returned — but
  the evaluation is recorded as an *error* (`harness.sensor.evaluations` with `verdict=error`, and an
  `error.type=sensor_error` event) instead of a pass. A response it cannot parse a score from is
  reported the same way — previously a pass with a "review inconclusive" reason, which only
  `ConsoleTracer` prints — and still charged to the run. `samples/AiToneSensor` no longer swallows
  either case (and now charges the tone model's spend to the run, as the guidance always said to), and
  the guidance that told sensor authors to catch and return `Pass` is corrected.

  **Behavioural change:** nothing changes for a harness built with `AddModelHarness` /
  `AddStandardModelHarness` beyond the telemetry verdict. If you supply your **own `ISensorRunner`**,
  or call `CriticSensor.CheckAsync` directly, a failed critic call now throws to you:

  ```csharp
  // Before — the critic never threw
  var result = await critic.CheckAsync(HookPoint.PreReturn, state, step, ct);

  // After — fail open yourself, as DefaultSensorRunner does
  SensorResult result;
  try { result = await critic.CheckAsync(HookPoint.PreReturn, state, step, ct); }
  catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
  catch (Exception ex) { result = SensorResult.Failed($"critic threw {ex.GetType().Name}: {ex.Message}"); }
  ```

- **A sensor failure is diagnosable in OpenTelemetry without `enableSensitiveData`.** The
  `gen_ai.evaluation.result` event for a failed sensor carried `error.type=sensor_error` but put the
  reason behind the content gate, so by default you could see *that* a sensor failed and not *why*.
  The reason of any error result is now always emitted as `exception.message`, the same way a guide
  error's already was. Intervention reasons stay gated — they can quote the content a sensor flagged.

  **Behavioural change:** the reason of an error result now leaves the process by default. That is
  two things — keep conversation content out of both:
  - **A `SensorResult.Failed(reason)` one of your own sensors returns.** Its reason used to be gated.
  - **The text of an exception a sensor throws.** Most exception messages describe the failure, not
    the conversation — an HTTP error, a malformed-JSON error, a missing property — but the BCL `Parse`
    methods (`int.Parse`, `decimal.Parse`, `bool.Parse`, `Enum.Parse`, …) quote the value they
    rejected. If a sensor of your own parses model output, use `TryParse` rather than letting such an
    exception escape.

### Added

- **`CompactionResult.Error` and `CompactionTrace.Error` — a failed compaction is no longer silent.**
  Compaction fails open twice over (the guide falls back to an omission note when a strategy throws;
  `AiCompactionStrategy` keeps the prior summary when its model call fails or returns nothing), and
  both paths did so without a trace — a compaction model that was down simply produced omission notes
  forever. The reason now travels on the result into the trace: `OpenTelemetryTracer` tags the
  `compact_context` event `error.type=compaction_error` with the summary in `exception.message`,
  `ConsoleTracer` adds `error` to its `compaction` line, and `LiveDashboardTracer` publishes a
  "Compaction fell back" line instead of "Compacted". Both properties are optional and `null` on
  success — additive, no migration. A custom `ICompactionStrategy` that degrades internally instead of
  throwing can set `CompactionResult.Error` to report through the same channel.

  **Behavioural change:** a pass that carries an `Error` now reports `Folded = false`.
  `AiCompactionStrategy` fails open by handing the prior summary back unchanged, which previously set
  `harness.compaction.folded=true` on a pass that had folded nothing.

## [2.4.0] — 2026-08-31

### Added

- **`WithLiveDashboardTracer()` — a zero-dependency in-process live dashboard feed.** A new
  `LiveDashboardTracer` (an `ITracer`, shipped in the existing `SapphireGuard.ModelHarness.Infrastructure`
  package — **not** a new package) streams every loop event as a `DashboardEvent` for a live agent
  console with no OTLP backend, collector, or Docker. It keeps a bounded ring buffer so a browser
  connecting mid-run replays the history, exposes `Subscribe(ct)` for a host endpoint (e.g.
  Server-Sent Events), and composes with `WithOtelTracer()` through `CompositeTracer`. Run/turn
  aggregation is left to the consumer, so the tracer stays transport-neutral — no web dependencies,
  just `System.Threading.Channels`.
  - `new LiveDashboardTracer(enableSensitiveData: true)` also includes each model response's text in
    its `model` event detail — the run's result — off by default, matching
    `WithOtelTracer(enableSensitiveData:)`. No model output leaves the process unless you opt in.
  - `samples/LiveDashboard` is the end-to-end reference: it wires `app.MapHarnessDashboard("/", onRun: …)`
    from the new `Dashboard` package (below) to serve the console — a searchable runs sidebar, a per-turn
    telemetry table, a turn-divided live trace with per-event drill-in, and the run's result.
  - Fully additive — no existing API changes, no migration needed.

- **`SapphireGuard.ModelHarness.Dashboard` — a new package: drop-in ASP.NET Core hosting for the live
  dashboard.** `app.MapHarnessDashboard(prefix = "/dashboard", onRun?)` serves the dashboard page, its
  assets, and the SSE feed — all embedded in the package, no `wwwroot` to copy — streamed from the
  DI-registered `LiveDashboardTracer`. A `<base href>` lets it live under any prefix. By default it is a
  read-only monitor of runs the host triggers elsewhere; pass `onRun` to reveal a "run a task" bar and map
  `POST {prefix}/run`. This is the only package that takes an ASP.NET Core dependency (`FrameworkReference
  Microsoft.AspNetCore.App`) — the transport-neutral `LiveDashboardTracer` stays in `Infrastructure`. Brings
  the published package count to **eight**.

## [2.3.1] — 2026-08-25

Maintenance release — dependency updates only. No API or behavioural changes, no migration needed.
Build is clean and all 479 unit tests pass; the Anthropic, structured-output, compaction, sensor,
multi-agent, and checkpoint/resume paths were smoke-tested against the live Anthropic API on the new SDK.

### Changed

- **Dependencies bumped to latest stable.** The consumer-facing floors that rise transitively are the
  adapter SDKs:
  - `Anthropic` 12.22.0 → 12.42.0 (Claude adapter)
  - `Azure.Identity` 1.13.2 → 1.21.0 (Azure OpenAI / AI Foundry adapter)
  - `OllamaSharp` 5.4.25 → 5.4.30 (Ollama adapter)
  - `Polly` 8.6.6 → 8.7.0 (resilience)
  - `Microsoft.Extensions.*` and `System.Text.Json` 10.0.8 → 10.0.11
  - Build-time only: `Microsoft.SourceLink.GitHub` 8.0.0 → 10.0.400
  - Test tooling: `Microsoft.NET.Test.Sdk` 17.14.1 → 18.9.0, `xunit.runner.visualstudio` 3.1.0 → 4.0.0

## [2.3.0] — 2026-07-21

### Added

- **`WithOtelTracer(enableSensitiveData, agentName)` — opt-in content capture and agent naming.**
  `OpenTelemetryTracer` now takes two optional settings (both additive; existing `WithOtelTracer()`
  calls compile and keep working):
  - `enableSensitiveData` (default `false`) captures conversation content on the spans:
    `gen_ai.input.messages` / `gen_ai.output.messages` (prompt and response bodies) on each `chat`
    span, `gen_ai.tool.call.arguments` / `gen_ai.tool.call.result` on each `execute_tool` span, the
    task text on the root span, and the sensor-reason text on evaluation events. Off by default, so
    no user or model content leaves the process unless you opt in — turn it on in development to
    inspect the actual exchange in your backend.
  - `agentName` (default `null`) stamps `gen_ai.agent.name` on the root `invoke_agent` span and into
    its display name (`invoke_agent {name}`), so several agents in one process are distinguishable.

### Changed

- **The OpenTelemetry tracer no longer emits conversation content by default** (behavioural change).
  Previously `harness.task.text` and the `gen_ai.evaluation.explanation` sensor reason were written
  to every trace unconditionally. They — and the newly added message / tool-I/O attributes — are now
  gated behind `enableSensitiveData`, which defaults to `false`. Error-path diagnostics (span status,
  the `exception` event, `harness.failure.reason`) are unaffected and always emitted.

  **Migration** — if you relied on `harness.task.text` or `gen_ai.evaluation.explanation` reaching
  your backend, opt back in:

  ```csharp
  // before — task text and sensor reasons were always exported
  builder.WithOtelTracer();

  // after — restore that (and additionally capture prompt/response bodies and tool I/O)
  builder.WithOtelTracer(enableSensitiveData: true);
  ```

  Leaving it at the default is the recommended production posture: content stays in your process.

## [2.2.1] — 2026-07-18

Closes the low-severity tail of the systematic audit — every finding from it is now fixed. Patch:
all fixes, no API added or removed. Most were latent, reachable only through a custom
implementation or an external caller; two change behaviour you can observe.

### Fixed

- **`TaintTrackingSensor` did not honour its own fail-closed contract.** At `PreToolCall` it called
  the `ITrustPolicy` unguarded, and sensors fail *open* by design — `DefaultSensorRunner` turns a
  throw into a non-intervention. A throwing custom policy therefore let the privileged action
  proceed while tainted content sat in the trajectory, the exact outcome the sensor exists to
  prevent. It now catches for itself and blocks when taint cannot be determined. The built-in
  list-based `TrustPolicy` does not throw, so only a custom `ITrustPolicy` was affected. (#16)

  **Behavioural change:** a privileged action that previously slipped through on a policy error is
  now blocked. This is the intended direction, but it can surface as a new intervention.

- **`CompositeRateLimiter` discarded a real `RetryAfter` behind a null one.** `check.RetryAfter >
  mostRestrictive.RetryAfter` is a lifted nullable compare, false whenever either side is null — so
  a limiter reporting "limited, duration unknown" won over one reporting a real 30s wait, and the
  loop fell back to its 10s default and under-waited. A non-null wait now always beats a null.
  Built-in limiters always report non-null, so only a custom `IRateLimiter` triggered it. (#16)

- **The turn index drifted +1 after a rate-limit backoff.** The loop incremented a counter every
  iteration, but the rate-limit `continue` appends no `ModelCallStep` — so the throttled iteration
  burned an index no model call used, and every subsequent loop-emitted span sat one bucket ahead of
  the guide-emitted events for the same turn (`DefaultGuideRunner` derives its index from the
  `ModelCallStep` count). The loop now derives the index the same way instead of counting, which
  also subsumes the resume seeding added in 1.1.1. Observability only. (#16)

- **`FileCheckpointStore.LoadAsync` was the unhardened sibling of `LoadLatestAsync`.** Three things,
  all on the by-ID path: it read its match directly, so a torn file threw where `ICheckpointStore`
  documents `null`; it took an arbitrary match (`Directory.GetFiles` guarantees no ordering) where
  the filename's embedded `CreatedAt` means one ID can have several files, so a superseded
  checkpoint could be resurrected — silently resuming from state the caller believed replaced; and
  it interpolated the caller's ID into a search glob unvalidated. It now skips torn files, returns
  the newest match, and rejects an ID containing wildcards or separators. (#16)

  **Behavioural change:** `LoadAsync` throws `ArgumentException` for an ID containing `* ? / \ :` or
  a null character. Framework-minted IDs are GUIDs and unaffected.

- **`StructuredOutputContract.TryBind` could pick a prose brace-group over the payload.** The
  fallback extractor took the *first* balanced value, so `Result {see below}: {"category":"billing"}`
  bound against `{see below}`, failed, and cost a repair turn. It now tries each balanced value in
  order until one deserializes. (#16)

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

### Breaking

*Recorded retrospectively — this change shipped in 1.0.0 but was only ever written up in
`docs/ROADMAP.md`, not here. See [ADR-002](docs/decisions/002-incremental-fold-compaction.md)
for the full reasoning.*

- **`ICompactionStrategy` became a fold: `SummariseAsync` → `CompactAsync`.** The 0.1.0 port was a
  stateless *view* — it received pre-rendered text for the whole evicted head and re-summarised it
  from scratch every turn, so compaction cost grew with the run and was attributed to no budget. The
  replacement passes a request record carrying the **typed** evicted steps, the full `AgentState`, and
  the summary accumulated so far, and returns a result that can persist an updated summary.

  ```diff
  - Task<string> SummariseAsync(
  -     int evictedStepCount,
  -     IReadOnlyList<string> evictedContent,
  -     int remainingTokenBudget,
  -     CancellationToken ct);
  + Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct);
  ```

  **Migration — a view (same behaviour as 0.1.0).** Return `UpdatedSummary = null` and nothing is
  persisted; the harness renders any prior summary in front of your text, so do not repeat it:

  ```csharp
  // Before
  public Task<string> SummariseAsync(
      int evictedStepCount, IReadOnlyList<string> evictedContent, int remainingTokenBudget, CancellationToken ct)
      => Task.FromResult($"[{evictedStepCount} steps omitted]");

  // After
  public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct)
      => Task.FromResult(new CompactionResult
      {
          InjectedText = $"[{request.EvictedSteps.Count} steps omitted]"
          // UpdatedSummary stays null — stateless view
      });
  ```

  The old parameters map as: `evictedStepCount` → `request.EvictedSteps.Count`,
  `remainingTokenBudget` → `request.RemainingTokenBudget`, and `evictedContent` → `request.EvictedSteps`,
  which are now typed `Step`s (`UserMessageStep`, `ModelCallStep`, `ToolCallStep`, `SensorInterventionStep`)
  rather than strings — render them yourself, or inspect them to compact structurally. Every trajectory
  opens with a `UserMessageStep`, so it is the first step evicted: a `switch` over the other three
  silently drops user turns from the summary.

  **Migration — a fold (recommended for long runs).** Summarise only `request.EvictedSteps` on top of
  `request.PriorSummary` and return a non-null `UpdatedSummary` whose `FoldedStepCount` is the new
  watermark. `InjectedText` must then be the *complete* replacement summary, not a delta. Report
  `Usage`/`Cost` if you call a model, so the spend reaches the budget enforcers.

  **Also required:** the opt-in methods now take `CompactionOptions`, so a strategy can never be wired
  without stating its eviction trigger:

  ```diff
  - builder.WithAiCompaction(modelClient);
  + builder.WithAiCompaction(modelClient, new CompactionOptions { WindowTokens = 100_000 });
  ```

  `Budget.MaxContextTokens` was renamed `Budget.MaxTotalTokens` in a sibling commit in this same
  release. It had meant both
  "size of one context window" (what eviction needs) and "cumulative tokens for the run" (what the
  enforcer checks); since cumulative is always ≥ one window, sharing the field hard-stopped compacted
  runs at roughly one window. The window size now lives in `CompactionOptions.WindowTokens`.

## [0.1.0] — 2026-06-05

Initial packaging release: multi-targeted `net8.0` + `net10.0`, MIT licence, SourceLink, and shared package
metadata across the seven publishable projects.

[2.2.1]: https://github.com/AnilKerai/model-harness/releases/tag/v2.2.1
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
