# ADR-001 — A collaborator's own timeout is not a cancelled run

**Status:** Accepted. Applied across the loop and both runners; the sensor case predates
the others, the rest landed in 2.5.0.

## Context

`HttpClient` reports a request timeout as `TaskCanceledException`, which derives from
`OperationCanceledException`. So does a genuinely cancelled run. The two are the same
exception type and mean opposite things:

- **The caller cancelled** — `ct.IsCancellationRequested` is `true`. Expected control
  flow. The run is over.
- **The callee timed out** — the harness token was never touched. A fault in one
  dependency, no different in kind from a `SocketException`, and every component already
  has a defined behaviour for a fault.

Almost everything the loop awaits can raise the second kind: the model client, a memory
or skill store reached through a guide, an AI sensor's internal model call, an
`ICompactionStrategy`'s fold call, the checkpoint store, the rate limiter, a tool, and a
custom `ITrustPolicy`.

Catching `OperationCanceledException` without distinguishing them produced four separate
bugs:

1. A slow memory or skill store ended the whole run as `Failed`/"cancelled" — the exact
   case the fail-open handling in `DefaultGuideRunner` exists to absorb.
2. A third-party `ICompactionStrategy` whose model call timed out took the run down
   instead of falling back to an omission note.
3. A model-client timeout returned `FailureReason = "cancelled"` though nothing had
   cancelled, and the `chat` span said only that the call did not complete.
4. In Polly, the opposite polarity of the same confusion: Polly's default predicate
   excludes every `OperationCanceledException`, and it records an outcome a predicate
   rejects as a **success** — so a backend that hung until `HttpClient` timed out never
   opened the circuit breaker. Fast-fail never engaged against the one failure mode it
   was there for.

## Decision

One two-clause shape, everywhere:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    throw;              // genuine cancellation — expected control flow
}
catch (Exception ex)
{
    // the callee's own timeout lands here, with every other fault
}
```

The guard is on the **harness token**, never on the exception type. Each component then
takes the path it already had for a fault:

| Component | A collaborator's own timeout becomes |
|---|---|
| `HarnessLoop` (model client, checkpoint store, rate limiter) | `Failed`, `FailureReason` = the exception's message; `IModelCallScope.Fail(ex)` puts `exception.type`/`message`/`stacktrace` on the `chat` span |
| `DefaultSensorRunner` | `SensorResult.Failed(reason)` — flagged `IsError`, **not** an intervention; the batch survives and the model keeps its turn |
| `DefaultGuideRunner` (supporting guides) | Guide skipped, contribution lost for that turn, logged via `LogGuideError` |
| `HeadEvictionTrajectoryGuide` (the fold call) | Fallback omission note carrying `CompactionResult.Error` |
| `HarnessLoop.ExecuteToolAsync` | Recoverable `IsError` `ToolResult` — the model replans |
| `TaintTrackingSensor` (fail-**closed**) | `Intervene` — blocks the privileged action |
| Polly breakers (`ResilientModelClientDecorator`, `ResilientTool`) | Counted as a failure, so the circuit can open |

Two components invert or narrow the rule, deliberately:

- **`TaintTrackingSensor` fails closed.** Fail-open would let a privileged action through
  while tainted content sits in context — the one outcome it exists to prevent. Its catch
  is the same two clauses inverted: genuine cancellation propagates, everything else
  returns `Intervene`.
- **`ResilientTool` counts its own timeout but does not retry it.** The call may already
  have run its side effect, and each retry would wait out the full timeout again.

## Consequences

- A sensor must **not** catch its own failure and return `Pass`. The run behaves
  identically, but the fault hides behind a clean result instead of being reported
  through `LogSensorResult` as `error.type=sensor_error`. `CriticSensor` lets a failed
  model call propagate to the runner, and reports an unparseable verdict as
  `SensorResult.Failed(...) with { Usage, Cost }` — non-blocking, still charged, but an
  error rather than a pass.
- A sensor error's reason is emitted as `exception.message` **ungated** by the sensitive-data
  flag, unlike an intervention reason, so a failing sensor is diagnosable without turning
  on content capture.
- The harness applies no second deadline to model calls. The provider SDKs already impose
  a request timeout, tunable per adapter via `ConfigureClient`. Adding one would duplicate
  a timeout and re-raise exactly this ambiguity.

## Alternatives rejected

- **`catch (Exception ex) when (ex is not OperationCanceledException)`** — the obvious way
  to write a fail-open catch, and wrong in both polarities. In a fail-**open** component it
  leaks the callee's timeout to the caller, failing the run. In a fail-**closed** one
  (`TaintTrackingSensor`) it leaks that case to the runner, which fails it open — turning a
  security gate into a pass.
- **Polly's default `OperationCanceledException` exclusion** — see failure 4. Only the
  caller's token (`args.Context.CancellationToken`) is excluded now.
- **A harness-level deadline on model calls** — see Consequences.
