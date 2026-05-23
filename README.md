# model-harness

A reusable model harness framework for .NET 10, structured around Clean / Onion
architecture.

> **Why "model harness"?** An *agent* is a model plus a harness — the harness
> is the scaffolding (loop, guides, sensors, budget) that wraps a model and
> turns it into an agent. Calling it an "agent harness" would imply the harness
> *is* the agent; "model harness" names what it actually is.

The samples under `samples/` wire up `ClaudeModelClient` against the Anthropic API
and `OllamaModelClient` against a local Ollama instance. A `FakeModelClient` is also
provided for local development with no external dependencies.

## Run it

Each scenario is its own console project under `samples/`. Add your Anthropic API
key to the `appsettings.local.json` of the sample you want to run, e.g.
`samples/HappyPath/appsettings.local.json`:

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  }
}
```

To run the Ollama scenario, add the Ollama model you want to use (the model
must be pulled locally with `ollama pull <model>`) to
`samples/OllamaToolCall/appsettings.local.json`:

```json
{
  "Ollama": { "ModelId": "qwen2.5:7b" }
}
```

Then run any sample by its project path:

```bash
dotnet run --project samples/HappyPath
dotnet run --project samples/OllamaToolCall
```

JSON trace events stream to stdout, followed by the final outcome and a
flattened trajectory.

---

## Architecture

Seven projects with a strict dependency direction:

![Architecture](docs/architecture.svg)

- **`SapphireGuard.ModelHarness.Framework`** — abstractions, the core loop, seven built-in
  guides, and `IServiceCollection` extension methods. Only external dependency
  is `Microsoft.Extensions.DependencyInjection.Abstractions`.
- **`SapphireGuard.ModelHarness.Infrastructure`** — concrete adapters: `FakeModelClient`,
  `ConsoleTracer`, `OpenTelemetryTracer`, `CompositeTracer`, `InMemoryToolRegistry`,
  `EchoTool`, `CalculatorTool`. Production sensors: `PiiRedactionSensor`
  (PostModelCall, scans for email/phone/card/NI/SSN patterns), `CostThrottleSensor`
  (PreModelCall, soft spend cap), `ToolResultSanityCheckSensor` (PostToolCall, validates
  result shape and custom per-tool rules), `StuckDetector` (PreToolCall, blocks repeated
  identical tool calls). Harness tools: `AskHumanTool` backed by `IHumanChannel`;
  `ConsoleHumanChannel` for development. Skills (procedural memory): `FileSkillStore`
  (persists `SKILL.md` files), and the `SkillManageTool` / `SkillViewTool` the model
  uses to capture and load skills. Depends on Framework only.
- **`SapphireGuard.ModelHarness.Infrastructure.Anthropic`** — Anthropic SDK adapter:
  `ClaudeModelClient` maps framework messages and tool definitions to the
  Anthropic Messages API and back. Depends on Framework only.
- **`SapphireGuard.ModelHarness.Infrastructure.Mcp`** — MCP adapter: `McpTool` wraps an
  `McpClientTool` as `ITool`; `McpToolFactory` lists tools from an MCP server and returns
  them as `IReadOnlyList<ITool>` ready to register. Depends on Framework + ModelContextProtocol.
- **`SapphireGuard.ModelHarness.Infrastructure.Persistence`** — checkpoint/resume:
  `FileCheckpointStore` persists each `AgentState` as a JSON file under
  `{dir}/{taskId}/{timestamp}_{id}.json`. A custom `StepJsonConverter` handles the polymorphic
  `Step` hierarchy without annotating the domain model. Wire up with
  `services.AddFileCheckpointStore(directory)`. Depends on Framework only.
- **`SapphireGuard.ModelHarness.Infrastructure.Resilience`** — `ResilientModelClientDecorator`:
  wraps any `IModelClient` with Polly retry (exponential back-off, 3 attempts) and circuit
  breaker (50% failure ratio, 15 s break). Depends on Framework + Polly v8.
- **`SapphireGuard.ModelHarness.Infrastructure.Ollama`** — Ollama adapter:
  `OllamaModelClient` maps framework messages and tool definitions to the OllamaSharp API.
  Handles Ollama's requirement that all tool calls from one model turn be grouped into a single
  assistant message, whereas the framework's trajectory emits them as interleaved
  `ToolUse`/`Tool` pairs. Wire up with `services.AddOllamaModelClient(options)`. Depends on
  Framework only.
- **`samples/*`** — one console project per scenario (HappyPath, PiiDetection, CostThrottle,
  ToolCallReasonableness, ToolResultSanity, CheckpointResume, OllamaToolCall, SkillLearning),
  each a composition root showing how a domain agent wires the framework via
  `Microsoft.Extensions.DependencyInjection`.

---

## Core patterns

The framework is built around two composable patterns that together give
fine-grained control over agent behaviour without modifying the loop.

### The Guide pattern — shaping perception

A **Guide** controls what the model sees on each turn. Before every model call,
all registered guides run in order, each contributing to a shared `ContextDraft`.
`DefaultContextBuilder` then assembles the draft into the final prompt.

```mermaid
flowchart LR
    A([All registered tools]) --> D

    subgraph "Guide pipeline — runs before every model call"
        direction LR
        D[ContextDraft\ninitialised] --> G1[SystemPromptGuide\nsets SystemPrompt]
        G1 --> G2[HarnessInstructionsGuide\nappends harness conventions]
        G2 --> G3[TrajectoryGuide\nrenders history]
        G3 --> G4[MemoryGuide\nsurfaces snippets]
        G4 --> G5[ToolSelectorGuide\nfilters AvailableTools]
        G5 --> G6[ToolCatalogueGuide\nrenders tool catalogue]
        G6 --> G7[SkillsGuide\nrenders skill catalogue]
        G7 --> GN[... custom guides]
    end

    GN --> CB[DefaultContextBuilder\nassembles prompt]
    CB --> M([Model call])
```

Each guide receives the full `ContextDraft` and the current `AgentState`, and
writes into one or more of the draft's fields:

| Field | Purpose |
|---|---|
| `SystemPrompt` | Agent identity and standing instructions |
| `TrajectoryMessages` | Rendered history — model turns, tool results, sensor notes |
| `MemorySnippets` | Long-term knowledge surfaced from a retrieval system |
| `AvailableTools` | Tool list for this turn — guides can filter or reorder |
| `SystemSections` | Pre-rendered system-prompt sections (tool catalogue, skill catalogue) appended after the prompt |

Implement `IGuide` to create a custom guide:

```csharp
public sealed class MyGuide : IGuide
{
    public string Name => "my-guide";

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        // e.g. filter tools based on the current turn count
        if (state.Trajectory.Count > 4)
            draft.AvailableTools.RemoveAll(t => t.Name == "search");

        return Task.CompletedTask;
    }
}

services.AddGuide<MyGuide>(); // runs after the seven built-in guides
```

Guides run **sequentially** so each one can build on what the previous added —
a tool-selector guide can, for example, inspect memory snippets before deciding
which tools to expose.

### The Sensor pattern — observing and intervening

A **Sensor** observes the loop at declared hookpoints and can raise a concern
by returning `SensorResult.Intervene(reason)`. The loop's response to that concern
depends on the hookpoint — sensors do not control flow directly. Sensors run in
**parallel** at each hookpoint — they observe independently and do not share state.

```mermaid
flowchart TD
    L([Loop reaches a HookPoint]) --> SR[ISensorRunner\nfans out in parallel]
    SR --> S1[Sensor A]
    SR --> S2[Sensor B]
    SR --> SN[Sensor N ...]
    S1 -- Pass --> M{Any intervene?}
    S2 -- Pass --> M
    SN -- Intervene: reason --> M
    M -- No --> CONT([Continue normally])
    M -- Yes --> INT[SensorInterventionStep\nappended to trajectory]
    INT --> NEXT([Next guide pass renders\nit as a system note])
```

The five hookpoints, their typical use, and what the loop does when a sensor intervenes:

| HookPoint | Fires | Typical use | On intervention |
|---|---|---|---|
| `PreModelCall` | Before building context and calling the model | Rate limiting, cost throttling | **The loop force-finalises** — it makes one model call with tools disabled so the model can answer from what it already knows, then returns `Done`. The loop cannot loop back here: no model call has happened yet, so the trajectory is unchanged from the previous turn — looping would be infinite. |
| `PostModelCall` | After the model responds, before acting on it | PII detection, output filtering | **Loops back** — the model gets another turn. Crucially, the flagged response text is **suppressed from the next context**: `TrajectoryGuide` omits it so the model cannot re-see flagged content. The intervention note still appears so the model knows why it was flagged. |
| `PreToolCall` | Before each tool is dispatched | Policy enforcement, authorisation | **Tool is never dispatched** — a `ToolCallStep` with `IsError = true` is recorded so the model sees a clean error result and can replan. |
| `PostToolCall` | After each tool result is received | Result validation, audit logging | **Advisory only** — the tool has already run and its result is already in the trajectory. The intervention is recorded as a system note; the model can still reason on the result. Use `PreToolCall` if you need to prevent execution. |
| `PreReturn` | Before returning a final answer to the caller | Answer quality checks | **Loops back** — the model retries. Unlike PostModelCall, the prior answer *is* visible in context so the model can see what it said and correct it. |

An intervention does not terminate the run. The sensor's reason is wrapped in a
`SensorInterventionStep` and appended to the trajectory. On the next turn,
`TrajectoryGuide` renders it as a system-role note prefixed `[HARNESS OBSERVATION — ...]`.
`HarnessInstructionsGuide` tells the model upfront (in the system prompt) what these
notes mean and that they must be treated as directives — this is the feedforward complement
to the sensor's feedback. Intervention records are separate from tool-call history so
tool history stays clean.

Implement `ISensor` to create a custom sensor:

```csharp
public sealed class MySensor : ISensor
{
    public string Name => "my-sensor";
    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PreToolCall };

    public Task<SensorResult> CheckAsync(
        HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is ToolCallStep tc && tc.Call.ToolName == "dangerous-tool")
            return Task.FromResult(SensorResult.Intervene("dangerous-tool is not permitted."));

        return Task.FromResult(SensorResult.Pass);
    }
}

services.AddSingleton<ISensor, MySensor>();
```

### How guides and sensors work together

Sensors intervene; guides determine what the model learns from that intervention.
The loop itself stays unaware of either pattern's semantics — it just runs the
runners and records the steps.

```
Sensor intervenes at PreToolCall
        │
        ▼
SensorInterventionStep appended to AgentState.Trajectory
        │
        ▼  (next turn)
TrajectoryGuide renders it as a system-role note in ContextDraft
        │
        ▼
Model sees: "[HARNESS OBSERVATION — my-sensor at PreToolCall] dangerous-tool is not permitted — adjust your next action and do not repeat flagged behaviour."
        │
        ▼
Model re-plans without that tool
```

---

## Skills — procedural memory (getting better over time)

A **skill** is a write-up of how to do something, saved after the agent works it out
once so it can reuse it next time instead of figuring it out again. The agent builds
up a small library of these over time. Nothing about the model itself changes — the
only thing that changes is what we show it.

Skills reuse the two patterns above: a **guide** shows the model which skills exist,
and a **tool** lets the model save and load them. The harness just facilitates — the
loop (`HarnessLoop`) doesn't even know skills exist.

```mermaid
flowchart LR
    STORE[(ISkillStore\nFileSkillStore persists SKILL.md)]
    STORE --> SG[SkillsGuide\nrenders catalogue\nname + when-to-use]
    SG --> CTX[Model sees the\nskill catalogue each turn]
    CTX -. calls .-> SM[skill_manage\nsave / delete]
    CTX -. calls .-> SV[skill_view\nload full body]
    SM --> STORE
    SV --> STORE
```

How it works, in one turn:

1. `SkillsGuide` shows the model a short list of saved skills — just the name and
   when to use each one (cheap, so it can sit in every prompt).
2. If the model wants one, it calls `skill_view` to read the full write-up.
3. When there's something worth keeping, the model calls `skill_manage` to save it.

`FileSkillStore` writes each skill to a `SKILL.md` file on disk, so skills stick
around between runs.

```csharp
// The read side ships on by default (no-op until a store is plugged in).
// Opt into the write side and a real store:
services
    .AddFileSkillStore("~/.skills")  // persists SKILL.md files
    .AddSkillTools();                // registers skill_manage + skill_view
```

See `samples/SkillLearning` for a runnable, no-API-key demo: run 1 saves a skill, and
run 2 loads it from disk and reuses it.

### Why it's built this way

The guiding rule: the harness handles **one task** (one "episode"); getting better
over many tasks is a separate job that lives *on top of* the harness, not inside it.
Every choice below keeps that learning logic out of the framework.

| Decision | Why |
|---|---|
| **Skills are notes, not code** | A skill is just text we drop into the prompt — not a new function that gets installed into the running agent. Nothing in the loop has to change, and a bad skill can't break anything — it's only text. |
| **The model decides to save — the harness just facilitates** | Remember *agent = model + harness*: it's the **model** that chooses to call `skill_manage`, and the harness simply dispatches the call and writes the file. The loop never forces a save or decides one is due. If you later want to automate that (e.g. save after a success), that's a layer you add on top — not something baked into the framework. |
| **On by default, but free until used** | The read side is always wired in, but the default store is empty — so the guide shows nothing and costs nothing until you plug in a real store. The tools and a real store are opt-in, like `AskHumanTool`. |
| **Show a short list, load on demand** | Every prompt carries only the skill names and when-to-use lines (cheap). The full write-up loads only when the model asks for it, so cost stays low even with lots of skills. |
| **Its own store, separate from memory** | Skills (named, with a body) are a different shape from memory snippets, so they get their own `ISkillStore` and the two can change independently. |

In short: **the harness stores, lists, and hands skills to the model. It never decides
when to save one, or whether the agent is "improving"** — the model makes that call.
Building anything smarter on top (like automatically saving after a success — see the
roadmap) is a layer you add, not part of the framework.

One nice side effect: doing the skills list as a guide let us move the **tool** list
into a guide too (`ToolCatalogueGuide`). Now every part of the prompt is built by a
guide, and the builder just glues the pieces together.

---

## The loop (`HarnessLoop`)

```mermaid
sequenceDiagram
    participant C as Caller
    participant L as HarnessLoop
    participant CP as ICheckpointStore
    participant B as IBudgetEnforcer
    participant S as ISensorRunner
    participant CB as IContextBuilder
    participant M as IModelClient
    participant R as IToolRegistry

    C->>L: RunAsync(AgentState)

    loop Each turn
        L->>CP: SaveAsync(Checkpoint{turn, state})
        L->>B: Check(state, startedAt)
        alt Budget exhausted
            B-->>L: Exhausted(reason)
            L->>CB: BuildAsync — force-finalise prompt
            L->>M: CallAsync (no tools)
            M-->>L: ModelResponse
            L-->>C: AgentOutcome { PartialResult }
        else Budget ok
            B-->>L: Ok
            L->>S: RunAsync(PreModelCall)
            S-->>L: Pass / Intervene → SensorInterventionStep
            L->>CB: BuildAsync(state, allTools)
            CB-->>L: ContextBuildResult(messages, selectedTools)
            L->>M: CallAsync(messages, toolDefinitions)
            M-->>L: ModelResponse
            L->>S: RunAsync(PostModelCall)
            S-->>L: Pass / Intervene → SensorInterventionStep
            alt No tool calls in response
                L->>S: RunAsync(PreReturn)
                S-->>L: Pass / Intervene → SensorInterventionStep
                L-->>C: AgentOutcome { Done, FinalAnswer }
            else Tool calls requested
                loop Each tool call
                    L->>S: RunAsync(PreToolCall)
                    alt Sensor intervenes
                        S-->>L: Intervene → SensorInterventionStep + ToolCallStep(IsError)
                    else Sensor passes
                        L->>R: DispatchAsync(call)
                        R-->>L: ToolResult
                        L->>S: RunAsync(PostToolCall)
                    end
                end
            end
        end
    end
```

Budget exhaustion is not an exception — `IBudgetEnforcer.Check` returns
`Exhausted(reason)` and the loop makes one final model call with tools disabled,
returning `AgentOutcome { Status = PartialResult }`. `BudgetExceededException`
is reserved for tools or sub-agents that violate budget from underneath the loop.

---

## Extending the framework

### Add a tool

```csharp
public sealed class MyTool : ITool
{
    public string Name => "my-tool";
    public string Description => "Does something useful.";
    public JsonElement InputSchema => JsonDocument.Parse("""{ "type": "object" }""").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, "result"));
}

services.AddSingleton<ITool, MyTool>();
```

### Add a sensor

```csharp
services.AddSingleton<ISensor, MySensor>();
```

`DefaultSensorRunner` picks it up automatically and runs it in parallel with
other sensors registered at the same hookpoint.

### Add a guide

```csharp
services.AddGuide<MyGuide>(); // runs after the seven built-in guides
```

### Enable checkpoint / resume

`HarnessLoop` auto-saves a `Checkpoint` at the start of each turn when an
`ICheckpointStore` is registered. The default is `NullCheckpointStore` (no-op).
Register `FileCheckpointStore` to write checkpoints to disk:

```csharp
services.AddFileCheckpointStore("/var/checkpoints");
```

Each checkpoint captures the fully-completed prior turn — `AgentState` plus an
envelope (`CheckpointId`, `RunId`, `TurnNumber`, `CreatedAt`). To resume after
a crash, load the latest checkpoint and pass its state back to a fresh harness:

```csharp
var store = new FileCheckpointStore("/var/checkpoints");
var checkpoint = await store.LoadLatestAsync(taskId);

var resumedState = checkpoint!.State with { Status = AgentStatus.Running };
var outcome = await harness.RunAsync(resumedState, ct);
```

The loop replays from the start of the interrupted turn (at-least-once
semantics — idempotent tools are safe; tools with side effects should be
designed accordingly).

Implement `ICheckpointStore` to target blob storage, a database, or any other
backend:

```csharp
services.AddCheckpointStore(_ => new MyBlobCheckpointStore(connectionString));
```

### Enable human-in-the-loop

`AskHumanTool` lets the model signal that it needs human input. Register it with
an `IHumanChannel` implementation — the channel decides how the question is
surfaced and how the response is collected:

```csharp
// Development — blocks on stdin
services.AddAskHumanTool<ConsoleHumanChannel>();

// Production — implement IHumanChannel for your delivery mechanism
services.AddAskHumanTool(_ => new SlackHumanChannel(slackClient, channelId));
```

`IHumanChannel` has one method: `AskAsync(question, ct) → string`. Everything
about *how* a human is reached is the caller's concern; the harness just
dispatches the tool call and feeds the response back to the model.

### Swap the model client

`IModelClient` only sees `ToolDefinition` records — it never touches `ITool`.
Implement it, translate `ToolDefinition.InputSchema` (`JsonElement`) into your
provider's tool-def format, and replace the registration:

```csharp
// Anthropic (wrap with resilience decorator for retry + circuit-breaking in production)
services.AddModelClient(_ => new ResilientModelClientDecorator(
    new ClaudeModelClient(new ClaudeClientOptions { ApiKey = apiKey, ModelId = "claude-haiku-4-5-20251001" })));

// Anthropic (simple — no resilience decorator)
services.AddClaudeModelClient(new ClaudeClientOptions { ApiKey = apiKey, ModelId = "claude-haiku-4-5-20251001" });

// Ollama (local — no resilience needed)
services.AddOllamaModelClient(new OllamaClientOptions { ModelId = "qwen2.5:7b" });

// Custom
services.AddModelClient(_ => new PollyResilientModelClient(new MyProviderClient(apiKey)));
```

### Resilience — the decorator pattern

`PollyResilientModelClient` wraps any `IModelClient` and adds retry and
circuit-breaking without the wrapped client knowing anything about it. This is
the **decorator pattern**: a class that implements an interface by delegating to
another implementation of the same interface, adding behaviour around it.

```
ResilientModelClientDecorator   ← adds retry + circuit breaker
        │
        └─▶ ClaudeModelClient           ← does the actual Anthropic API call
                 (or FakeModelClient, or any other IModelClient)
```

Because resilience lives in the decorator rather than in `ClaudeModelClient`
itself, it applies automatically to any provider — swap the inner client and
you get the same retry behaviour for free. It also keeps each class focused on
one job: `ClaudeModelClient` handles the Anthropic API, `PollyResilientModelClient`
handles failure.

The two behaviours it adds:

- **Retry** — on a network error or timeout, waits 200 ms then tries again,
  doubling the wait each time, up to 3 retries.
- **Circuit breaker** — if more than 50% of recent calls fail, stops trying
  immediately for 15 seconds rather than hammering a broken endpoint. Lets one
  call through after the break to check if the service has recovered.

### Composition pattern

Two patterns per **single-instance** abstraction:

- `AddXxx<TImpl>()` / `AddXxx(factory)` — explicit override, uses `Replace`.
- `AddXxxDefault()` — framework default via `TryAdd`; any prior explicit registration wins.

**Guides are a collection**, so the pattern differs: `AddXxxGuideDefault()` uses plain
`AddSingleton` (not `TryAdd`). Opt-out by not calling the default. Custom guides
append with `AddGuide<T>()`.

`AddModelHarness(systemPrompt)` aggregates everything. You still need to register
`IModelClient`, `IToolRegistry`, `ITracer`, and your `ITool` / `ISensor` instances.

```csharp
services
    .AddModelHarness(systemPrompt)
    .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddModelClient(_ => new PollyResilientModelClient(new MyModelClient()));

services.AddSingleton<ITool, MyTool>();
services.AddSingleton<ISensor, MySensor>();
services.AddGuide<MyCustomGuide>(); // optional
```

---

## Roadmap

See [ROADMAP.md](ROADMAP.md) for what's done and what's still to implement.

---

## Harness concerns vs. user concerns

Understanding what the framework owns and what it deliberately leaves to the user is key to extending it correctly.

### Harness concerns — the framework owns these

These are things every agent needs, regardless of domain. The framework provides them and they are always present.

| Concern | Where it lives | Status |
| ------- | -------------- | ------ |
| Turn-by-turn loop orchestration | `HarnessLoop` | ✅ |
| Budget enforcement (turns, tokens, cost, wall clock) | `IBudgetEnforcer` / `DefaultBudgetEnforcer` | ✅ |
| Context assembly — what the model sees each turn | Guide pipeline / `IContextBuilder` | ✅ |
| Trajectory rendering and compaction | `TrajectoryGuide` | ✅ |
| Sensor observation and intervention routing | `ISensorRunner` / hookpoints | ✅ |
| Tool dispatch | `IToolRegistry` | ✅ |
| Model transport | `IModelClient` | ✅ |
| Tracing and metrics | `ITracer` / `CompositeTracer` | ✅ |
| Checkpoint / resume | `ICheckpointStore` / `FileCheckpointStore` | ✅ |

Infrastructure projects ship concrete implementations of these seams. They are conveniences — a user could write their own — but they are implementations of harness-level abstractions and belong in this repo.

### User concerns — the framework does not own these

These are things that vary by agent, deployment, or domain. The framework provides the seam; the user provides the implementation.

| Concern | Seam | Notes |
| ------- | ---- | ----- |
| Sub-agents / A2A | `ITool` | A local sub-agent is an `ITool` whose `ExecuteAsync` runs another `HarnessLoop`. A remote one calls an A2A endpoint. The framework has no opinion on which — it just dispatches the tool call. |
| Long-term memory | `IMemoryStore` → `MemoryGuide` | Replace `NullMemoryStore` with a vector store or knowledge graph. |
| Procedural memory / skills | `ISkillStore` → `SkillsGuide` + `skill_manage` / `skill_view` | Replace `NullSkillStore` with `FileSkillStore` or a custom backend. The harness surfaces, loads, and persists skills; the **model** decides *when* to save one (the harness just dispatches the call). Any cross-episode automation on top is the user's. |
| Tool relevance ranking | `IToolSelector` → `ToolSelectorGuide` | Filter or rerank `ContextDraft.AvailableTools` per turn. |
| Domain sensors | `ISensor` | Business rules, authorisation checks, output quality gates — all per-agent. |
| Domain tools | `ITool` | Everything the model can invoke. |
| Human-in-the-loop | `IHumanChannel` → `AskHumanTool` | The harness provides the seam and a `ConsoleHumanChannel` for development. How a question is surfaced — stdin, Slack, webhook, queue — is a system design decision the harness cannot make. See the FAQ. |

---

## FAQ

### Why is budget enforcement a separate code path rather than a `PreModelCall` sensor?

Three reasons, each independent:

**1. Different outcome semantics.** Budget exhaustion returns `AgentOutcome { Status = PartialResult }`, signalling the agent was cut off before it could finish. A sensor block at `PreModelCall` returns `Done`, signalling the agent reached a stopping point. Those mean different things to a caller — `PartialResult` is "we ran out of resources", `Done` is "a policy said stop here and the model wrapped up cleanly". If budget enforcement went through the sensor path, both cases would return `Done` and callers would lose the ability to distinguish them.

**2. Ordering — budget is checked before sensors run.** The loop does `budget check → sensors`, in that order. If budget enforcement were a sensor it would run in parallel with other sensors, which could do work (memory queries, validation calls) before the budget check fires. When the budget is already gone, running sensors at all is wasteful. The explicit check short-circuits everything before any sensor touches the state.

**3. Unconditional vs. pluggable.** Sensors are opt-in — you compose them per agent, and a minimal agent can register none. The budget enforcer is always wired in by `AddModelHarness`; it is a framework guarantee, not a composition choice. If cost throttling were the only enforcement mechanism, a misconfigured agent with no sensors would have no hard ceiling.

### If ordering matters, shouldn't cost throttling and rate limiting also be explicit checks rather than sensors?

The ordering argument is strong against making budget enforcement a sensor; it is much weaker the other way around. Cost throttle and rate limit sensors are typically the cheapest operations in the `PreModelCall` batch (summing decimals from the trajectory) — there is no meaningful wasted work before they fire.

More importantly, pulling them into the explicit sequential path would require the loop to know about them by name, and every agent would get the same thresholds. Keeping them as sensors means each agent can register its own configuration — a background summarisation agent might tolerate a $0.50 soft cap, while a customer-facing agent runs on $0.05. You can also stack multiple policies (a per-task cost cap alongside a per-minute rate limit) without the loop knowing anything about either. First-class checks cannot do this cleanly.

The asymmetry is intentional: budget enforcer → first-class because it is an unconditional framework guarantee with a fixed shape; cost throttle / rate limit → sensors because they are optional, per-agent policies that vary in threshold, number, and kind.

### Why is human-in-the-loop a user concern rather than a harness concern?

Because HITL is a **system design concern**, not a **model control concern** — and the harness only owns the latter.

The harness controls the model: it shapes what the model perceives (guides), observes and constrains what it does (sensors), enforces resource limits (budget), and records everything (trajectory). That is its complete job.

HITL is about what happens *outside* the model loop: how a question is surfaced to a human, over what channel, how long you wait, and how the response is routed back. A CLI tool, a Slack bot, a web app, and an async queue-based pipeline all have fundamentally different answers. The harness cannot make that decision without becoming an application framework.

The boundary: the harness provides `AskHumanTool` backed by `IHumanChannel`. When the model decides it needs human input, it calls the tool. What `IHumanChannel.AskAsync` does — block on stdin, post to Slack, write to a queue and suspend — is entirely the user's implementation. The harness ships `ConsoleHumanChannel` for development, the same way it ships `FakeModelClient`: useful locally, not a production prescription.

### Should a harness even have a skills system — isn't "learning" out of scope?

The harness doesn't do the learning. It just provides a few simple building blocks,
and the model uses them.

Here's the easiest way to think about it. The harness handles **one task at a time**.
"Getting better over time" happens **across many tasks**. Those are two different
jobs, so we keep them separate — the harness does the first, and anything that spans
many runs is built on top of it.

All the harness adds is three small, ordinary things:

- a place to store skills (`ISkillStore`),
- a tool the model can call to save or load a skill (`skill_manage` / `skill_view`),
- a guide that lists the saved skills so the model knows they exist (`SkillsGuide`).

None of these are special "learning" machinery. A store is just storage; the tools
are just the model reading and writing notes; the guide just shows a list. The
harness never decides *when* to save a skill or whether the agent is "improving" —
the model makes those calls, and any smarter logic (like automatically saving a
skill after a success) lives in your own code, not in `HarnessLoop`.

A handy test we used on every piece: *"would this still be useful if we deleted the
word 'learning'?"* Each one passed — they're all useful plumbing on their own. The
day a learning algorithm has to live inside the loop itself is the day we've gone
too far, so we don't.

---

## Glossary

| Term | Definition |
|---|---|
| **Agent** | An Agent = Model + Harness. A loop-driven process that takes a natural-language task, uses tools and a model to produce a result, and records every step it takes. |
| **Harness** | The scaffolding that wraps a model: loop, guides, sensors, and budget. The harness is a model control concern — it does not own application or system design decisions. |
| **Turn** | One iteration of the loop: build context → call model → act on response. Each turn appends one or more `Step`s to the trajectory. |
| **Episode** | One full run of the agent on a single task — from the first turn to the final `AgentOutcome`. An episode is usually several turns. "Across episodes" means across many separate runs (e.g. reusing a skill saved in an earlier run). |
| **Trajectory** | The append-only, ordered list of `Step`s on `AgentState`. It is the durable log of everything the agent has done and seen. Three step types: `ModelCallStep` (a model call and its response), `ToolCallStep` (a tool invocation and its result), `SensorInterventionStep` (a sensor concern and its reason). |
| **AgentState** | Immutable record of the agent's full state at a point in time. New state is produced each turn — the trajectory is the log of those transitions. |
| **AgentOutcome** | The terminal result of a run: final answer, status (`Done`, `PartialResult`, `Failed`), and the last `AgentState`. |
| **Budget** | Hard limits on a run: `MaxTurns`, `MaxContextTokens`, `MaxCostUsd`, `MaxWallClock`. Checked at the top of every turn before any sensor or model call. |
| **Guide** | Shapes what the model perceives. Guides run sequentially before each model call, each contributing to a shared context draft — system prompt, trajectory, memory snippets, available tools. |
| **Sensor** | Observes the loop at declared hookpoints and can raise a concern. Sensors run in parallel; the loop's response to a concern depends on the hookpoint (see the hookpoint table). |
| **HookPoint** | A lifecycle position where sensors fire: `PreModelCall`, `PostModelCall`, `PreToolCall`, `PostToolCall`, `PreReturn`. |
| **Tool** | Something the model can invoke by name. The harness dispatches the call; the model decides when and why to use it. |
| **Skill** | A reusable procedure (procedural memory) the agent captures from past work and reuses later. Stored as data (a `SKILL.md` document) and surfaced into the prompt by `SkillsGuide`, never executed as code. Created by the model via `skill_manage`; loaded on demand via `skill_view`. |
| **Model client** | The transport seam (`IModelClient`). Receives a message list and tool definitions; returns a response. Knows nothing about state, the loop, or tool implementations. |
| **Checkpoint** | A durable snapshot of `AgentState` saved at the start of each turn. Used to resume a run after a crash or restart — pass the loaded state back to a fresh `HarnessLoop`. |
