# ADR-004 — A response is a final answer only when it ended normally

**Status:** Accepted. Replaces the `MaxTokens`-only check that shipped briefly on `main`
(6f8af53) and was never tagged.

## Context

`HarnessLoop` read "no tool calls" as "the model is finished" and never consulted
`ModelResponse.StopReason` — only the tracers did. A response the provider cut off at
`max_tokens` therefore returned `Done`, with a `FinalAnswer` ending mid-sentence and a null
`FailureReason`.

The first fix special-cased `MaxTokens`: a denylist of one. Review found it incomplete in three
ways, and all three had the same cause.

1. **Unrecognised reasons still meant "finished".** Every adapter mapped any reason it did not
   enumerate to `EndTurn`. Anthropic's `refusal` and `model_context_window_exceeded` — which
   [Anthropic's docs](https://docs.claude.com/en/api/handling-stop-reasons) say to "treat as
   truncated" — and Azure's `content_filter` all still returned `Done`. A refusal came back as a
   successful answer.
2. **Ollama never reported truncation at all.** Its adapter inferred the stop reason from the
   presence of tool calls and ignored `done_reason`, though it sends the cap as `num_predict`.
3. **It dispatched tool calls from a truncated response**, on the premise that the model had
   managed to emit them. But `max_tokens` means generation was cut mid-stream; when Claude is
   actually calling a tool the stop reason is `tool_use`. A trailing call may carry partial
   arguments — and on Azure the truncated JSON threw in the adapter, failing the run with a
   parser message before the loop could say why it stopped.

The common cause is the denylist. Every reason nobody had thought to list defaulted to "done",
so the fix kept failing open on the next one.

## Decision

**An allowlist in the loop.** `EndTurn`, `StopSequence` and `ToolUse` are the only normal
endings. Every other value — including `Other`, and any member added later — returns
`PartialResult` with a `FailureReason` naming the cause and, where there is one, the remedy.
The default arm of the switch is what makes it an allowlist.

**Where it runs.** After the `PostModelCall` sensors, so a content gate (PII redaction) still
applies to a cut-short response. Before tool dispatch, so no partial call runs. Before the
`PreReturn` sensors, because a validator (the `WithStructuredOutput<T>` repair loop) would
challenge a remnant the model cannot repair under the same cap, and the run would report the
intervention limit instead of the real cause.

**No extra model call.** Re-asking under the same cap truncates again. When budget finalisation's
own response is cut short, the same reason is appended to the budget reason.

**The harness's own model calls follow the same rule.** `AiCompactionStrategy` fails open on a
summary that did not end normally rather than persisting it — a fold is permanent, so a truncated
or refused summary would replace history it never actually covered. `CriticSensor` needs no change:
it accepts a verdict only if it parses with a numeric score, so a refusal or a verdict cut off
mid-JSON is reported as a sensor error, never a pass — and a verdict that completed before the
cut is complete.

**Adapters map unknown to `Other`, never `EndTurn`.** Each adapter's mapping lives in its
`*WireFormat` class and is unit-tested. `Refusal`, `ContentFilter` and `ContextWindowExceeded`
were added to `StopReason` because each needs a different remedy; they are **appended**, because
checkpoints persist the enum as an integer and reordering would silently remap stored responses.

## Consequences

What this costs, and what it makes harder:

- **A new normal ending needs a code change.** If a provider adds a stop reason that does mean
  "finished", runs report `PartialResult` until it is mapped. That is the safe direction to fail —
  the caller sees an anomaly instead of trusting it.
- **Some answers that used to be "good enough" are now partial.** A nearly-complete truncation,
  or a refusal with an explanation, no longer arrives as `Done`. `FinalAnswer` is still populated
  on `PartialResult`, but a caller that only reads it on `Done` loses those answers.
- **Exhaustive switches over `StopReason` break** for any consumer that matched every member
  without a discard: CS8509 at compile time, `SwitchExpressionException` at run time.
- **Ollama servers that predate `done_reason`** keep the old tool-call inference, so truncation
  on them stays invisible. A missing value is treated as "the server did not say", not as abnormal.
- **`pause_turn` maps to `Other`.** Correct — the response is incomplete — but the harness has no
  continuation for it. It is unreachable today because the Anthropic adapter declares client tools
  only, and `pause_turn` follows server-tool loops.

## Alternatives rejected

- **Enumerate more bad reasons.** A denylist of N fails open on reason N+1. This is exactly how the
  bug survived its first fix.
- **Dispatch the well-formed calls and drop only the truncated last one.** The Anthropic SDK hands
  back parsed input with no truncation marker, so "which call is partial" is not knowable from the
  response. And running part of a multi-call plan the model never finished acts on an incomplete
  intent. Stopping is safe; the run can be repeated with a higher cap.
- **A continuation turn** — append the partial text and ask the model to continue. The real fix for
  long outputs, but it needs its own cap and interacts with the consecutive-intervention counter.
  Deferred until long single-response output is a real workload.
- **Automatic fallback to another model on refusal.** Which model, at what cost, under what policy
  are deployment decisions. The reason names the remedy and the caller decides.
- **Collapse every abnormal ending to `Other`.** Loses the remedy. "Raise `MaxOutputTokens`",
  "lower `WindowTokens`" and "try another model" are different actions, and the `FailureReason`
  should say which.
