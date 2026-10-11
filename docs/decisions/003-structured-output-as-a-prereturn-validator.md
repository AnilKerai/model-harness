# ADR-003 — Structured output is a `PreReturn` validator, not a generator constraint

**Status:** Accepted. `WithStructuredOutput<T>()` landed in 1.1.0.

## Context

Constraining a run's final answer to a type `T` looks like it needs a new port. It does not,
and the reason is a property of this loop that other frameworks' loops do not have.

Every framework surveyed — `Microsoft.Extensions.AI`, Semantic Kernel,
`Microsoft.Agents.AI`, Pydantic AI, the OpenAI Agents SDK, LangChain `create_agent`, the
Vercel AI SDK — broadcasts its output schema on **every** model call in the tool loop. They
have to: a *generator* must be constrained before the call, and none of them knows which
turn will be the last one. That exposes all of them to a turn-1 short-circuit, where the
model satisfies the schema immediately and never touches its tools (vercel/ai#8354:
*"anthropic models wouldn't call the tools"*). None detects, prevents, or documents it.

A *validator* only needs to know **after** the call. And this loop reaches `PreReturn`
only on a turn with no tool calls — which is *the final turn, known with certainty*.

The second half of the problem is what to do with an answer that does not conform. MEAI,
Semantic Kernel, `Microsoft.Agents.AI`, the OpenAI Agents SDK and Vercel all deserialize
client-side post-hoc and either throw or hand back a `bool`, with **no feedback to the
model at all**.

## Decision

One `StructuredOutputContract<T>` — JSON Schema, `JsonSerializerOptions`, and `TryBind` —
shared by three consumers, so the thing that *states* the contract, the thing that
*enforces* it, and the caller that *reads* it cannot drift. No new port, no loop change,
no tool.

**`StructuredOutputGuide<T>` states the schema as a system section.** That placement is
load-bearing, not incidental: system sections are rebuilt from scratch every turn and
`HeadEvictionTrajectoryGuide` only ever trims the *trajectory*, so the contract is
unreachable by eviction **by construction**. This is the structured-output instance of the
sensor↔eviction trap — a contract evicted out from under the sensor enforcing it — and it
needs no pin. `ToolResult.Pins` exist to bridge *trajectory-borne* content (a `skill_view`
result) into the persistent region; a guide's contribution is already there. Pins are for
content the model discovered; guides are for content you declared.

**`StructuredOutputSensor<T>` binds at `PreReturn`.** Exactly-final-turn enforcement, no
broadcasting, intermediate reasoning turns left entirely unconstrained.

**Repair, not throw.** A non-binding answer is `InterveneWithToolSuppression`d: the
binder's own error — which names the missing or malformed member — goes back as a
`SensorInterventionStep`, the model gets a fresh turn with tools suppressed so it can only
reformat, and the loop's existing consecutive-intervention cap bounds it to `PartialResult`
rather than an exception. No new counter was needed.

## Consequences

- **`RespectRequiredConstructorParameters = true` is the whole reason this is a validator
  and not a no-op.** Without it, System.Text.Json treats every constructor parameter as
  optional: `Deserialize<Triage>("{}")` yields `Triage { Category = null, Priority = 0 }`
  and does not throw, so the validation silently becomes a rubber stamp. Paired with
  `RespectNullableAnnotations`, per the .NET guidance for new code. A unit test guards it —
  do not remove that test.
- **Binding is lenient where leniency is safe and strict where it is not.** `TryBind`
  strips markdown code fences and extracts balanced JSON values from surrounding prose,
  **trying each in order until one deserializes** — a brace group in the preamble
  (`Result {see below}: {...}`) is not the payload, and betting the bind on the first match
  cost a needless repair turn. Weak models emit fences and prose regardless of instruction;
  Instructor treats this as a first-class transport (`Mode.MD_JSON`). Base options inherit
  `JsonSerializerOptions.Web` for case-insensitive matching and numbers-from-strings.
- `[Description]` on the type and its members flows into the schema as `description`
  keywords, so the contract documents itself to the model.
- Non-object roots (`List<int>`, an enum) need no wrapper — the text channel carries any
  JSON value, unlike a tool-input schema, which must be an object.
- A truncated final answer is **not** a binding failure and must not be treated as one. A
  response cut off at the provider's output-token cap is caught before the `PreReturn`
  sensors run, precisely so this sensor does not challenge a remnant it cannot repair. See
  `CLAUDE.md`, HarnessLoop invariants.

## Alternatives rejected

- **A "final answer" tool.** This was the first design and it lost. It needs a terminal
  `ToolResult` flag, a loop change, a guard so `ToolSelectorGuide` cannot drop the tool and
  strand the run, and a decision about co-emission with real tool calls (Pydantic AI's
  `end_strategy`). The sensor gets the same guarantee — including "you may not end this run
  with prose", which is Pydantic AI's `tool_choice='required'` — for none of that.
- **Provider-native structured output as the primary mechanism.** It is the documented
  *upgrade path*, not a replacement: a default-interface method on `IModelClient` (the same
  back-compat idiom as `ITracer.LogGuideContribution`), additive, adapters opting in one at
  a time — Ollama's `format` is grammar-constrained decoding and the strongest of the three,
  Azure has `response_format: json_schema`, Anthropic has structured outputs. The sensor
  stays valuable alongside it, because native is not a guarantee: MEAI's own XML docs say
  *"Language models are not guaranteed to honor the requested schema,"* and every framework
  relying on native mode has zero repair when it misses. Note for whoever adds it to Azure:
  structured output there requires `parallel_tool_calls: false`, a live footgun none of the
  three Microsoft frameworks handles for you.
- **LangChain's unbounded repair.** `ToolStrategy` appends `"Error: {error}\n Please fix
  your mistakes."` with no retry counter, bounded only by a graph recursion limit of 25.
  The feedback is right; the missing bound is the part not to copy.
