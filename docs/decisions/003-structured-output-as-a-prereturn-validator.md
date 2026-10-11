# ADR-003 — Structured output is a `PreReturn` validator, not a generator constraint

**Status:** Accepted. `WithStructuredOutput<T>()` landed in 1.1.0.

## Context

Constraining a run's final answer to a type `T` looks like it needs a new port. It does not,
and the reason is a property of this loop that other frameworks' loops do not have.

Every framework surveyed — `Microsoft.Extensions.AI`, Semantic Kernel,
`Microsoft.Agents.AI`, Pydantic AI, the OpenAI Agents SDK, LangChain `create_agent`, the
Vercel AI SDK — *constrains generation* to its output schema on **every** model call in the
tool loop (`response_format`, or a required final-answer tool). They have to: a *generator*
must be constrained before the call, and none of them knows which turn will be the last one.
That exposes all of them to a turn-1 short-circuit, where the model satisfies the schema
immediately and never touches its tools (vercel/ai#8354: *"anthropic models wouldn't call the
tools"*). None detects, prevents, or documents it.

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

**`StructuredOutputSensor<T>` binds at `PreReturn`.** Enforcement happens only on the final
turn. The schema *is* stated every turn — the model has to know the contract — but as text in
the system prompt, never as a constraint on generation, so intermediate reasoning and
tool-calling turns stay free.

**Repair, not throw.** A non-binding answer is `InterveneWithToolSuppression`d: the
binder's own error — which names the missing or malformed member — goes back as a
`SensorInterventionStep`, the model gets a fresh turn with tools suppressed so it can only
reformat, and the loop's existing consecutive-intervention cap bounds it to `PartialResult`
rather than an exception. No new counter was needed.

**Lenient about transport, strict about shape.** Weak models wrap JSON in markdown fences and
surround it with prose regardless of instruction — Instructor treats this as a first-class
transport (`Mode.MD_JSON`) — so `TryBind` strips fences and extracts balanced JSON values,
trying each candidate in turn. Betting on the first match was tried and cost a needless repair
turn whenever a brace group in the preamble (`Result {see below}: {...}`) came first. Shape, by
contrast, is strict: without `RespectRequiredConstructorParameters`, System.Text.Json treats
every constructor parameter as optional and `Deserialize<Triage>("{}")` succeeds as
`Triage { Category = null, Priority = 0 }` — the validator would be a rubber stamp.

## Consequences

The invariants this decision imposes are stated in `CLAUDE.md` (Structured output) and not
repeated here. What the decision costs:

- **The schema rides in every prompt.** It is validated once, on the final turn, but stated on
  every call — prompt tokens each turn (a cache read on Anthropic once the prefix is warm).
- **Conformance rests on obedience.** A text instruction is not a grammar. A model that cannot
  follow the schema burns repair turns up to the intervention cap; the native-output upgrade path
  that would help it is documented, not built.
- **Repair can only reformat.** Tools are suppressed on the repair turn, so an answer that is
  wrong because data is *missing* cannot be fixed by repair — only one that is badly shaped.
- **Strictness is only as good as the type.** `RespectRequiredConstructorParameters` makes
  constructor parameters required; a type built from `init` properties without `required` still
  binds `{}`. The guarantee depends on how `T` is declared.
- **Each repair is a paid model call.** The intervention cap allows two repairs; a third
  challenge triggers one forced finalisation call and returns `PartialResult`.

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
