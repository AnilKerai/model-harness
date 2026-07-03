# CLAUDE.md — model-harness

## What this is

**SapphireGuard.ModelHarness** is a .NET 10 agent framework that wraps LLMs with structured scaffolding. An *agent = model + harness* — the harness (loop, guides, sensors, budget) is what turns a raw model into a controllable agent.

## Build & run

```bash
dotnet build SapphireGuard.ModelHarness.slnx
dotnet run --project samples/HappyPath
```

Add an API key first (per-sample; samples fall back to `FakeModelClient` when absent):

```json
// samples/HappyPath/appsettings.local.json
{ "Anthropic": { "ApiKey": "sk-ant-..." } }
```

## Solution structure

| Project | Role |
|---|---|
| `Framework` | Abstractions + core loop only. Only external dep: `Microsoft.Extensions.DependencyInjection.Abstractions` |
| `Infrastructure` | Concrete implementations: `FakeModelClient`, `ConsoleTracer`, `OpenTelemetryTracer`, `CompositeTracer`, `InMemoryToolRegistry`. Sensors: `StuckDetector`, `ProgressCheckSensor`, `PiiRedactionSensor`, `ToolResultSanityCheckSensor`, `PromptInjectionSensor`, `TaintTrackingSensor`, `CriticSensor`, `MonologueLoopSensor`, `AlternatingToolLoopSensor`, `ToolErrorLoopSensor`. Harness tools: `AskHumanTool`, `ConsoleHumanChannel`. Skills (procedural memory): `FileSkillStore`, `SkillManageTool`, `SkillViewTool`. Multi-agent: `AgentFactory` (spawns sub-agents) + `AgentTool` (exposes a sub-agent as an `ITool`), wired via `AddAgentFactory` / `AddSubAgentAsTool`. Depends on Framework only |
| `Infrastructure.Resilience` | `ResilientModelClientDecorator` — wraps any `IModelClient` with Polly retry + circuit breaker. Depends on Framework + Polly v8 |
| `Infrastructure.Anthropic` | Anthropic SDK adapter (`ClaudeModelClient`). Depends on Framework only |
| `Infrastructure.Persistence` | Checkpoint/resume (`FileCheckpointStore`, `StepJsonConverter`). Depends on Framework only |
| `Infrastructure.Ollama` | Ollama adapter (`OllamaModelClient`) via OllamaSharp v5. Depends on Framework only |
| `Infrastructure.AzureOpenAI` | Azure AI Foundry / Azure OpenAI Service adapter (`AzureOpenAIModelClient`) via `Azure.AI.OpenAI` v2. Supports API key and `DefaultAzureCredential`. Depends on Framework only |
| `samples/*` | One console project per scenario — composition roots showing how to wire everything via DI |
| `getting-started/` | Standalone minimal on-ramp (`GettingStarted.csproj` with its own `.slnx`) — the smallest end-to-end example, kept outside the main solution |

Dependency direction is strict and unidirectional: samples/* → Infrastructure / Infrastructure.Anthropic / Infrastructure.Ollama / Infrastructure.AzureOpenAI / Infrastructure.Resilience → Framework.

MCP has no dedicated project — the `Infrastructure.Mcp` adapter was removed (trivial, unused); MCP tools are integrated via the tool registry / `ITool` pattern documented in `docs/EXTENDING.md`.

## Core patterns

### HarnessLoop
Turn-by-turn state machine: check budget → sensors (PreModelCall) → build context via guides → call model → sensors (PostModelCall) → dispatch tools → sensors (Pre/PostToolCall) → repeat. Budget exhaustion is control flow, not an exception — one final model call with tools disabled, then `PartialResult`.

### Guide pattern
Guides run **sequentially** before each model call, each contributing to a shared `ContextDraft`. Pipeline order is explicit and fixed: `HarnessInstructionsGuide` → `ReActGuide` → `MemoryGuide` → `ToolSelectorGuide` → `ToolCatalogueGuide` → `SkillsGuide` → [custom guides] → `HeadEvictionTrajectoryGuide`. `HeadEvictionTrajectoryGuide` is always last so it can measure all prior contributions and compute an accurate token budget. `ToolSelectorGuide` must precede `ToolCatalogueGuide` — the catalogue renders whatever tools the selector has approved. `ReActGuide` only appends Thought/Action/Observation framing to the system prompt, so its slot among the prompt-building guides is not load-bearing. Add custom guides with `builder.WithGuide<T>()`; they slot in before `HeadEvictionTrajectoryGuide`. After each guide (and the trajectory guide) runs, `DefaultGuideRunner` emits a `GuideContribution` via `ITracer.LogGuideContribution` — the structural delta that guide made to the draft (tools added/removed by name, memory snippets, system sections, trajectory messages, system-prompt char delta) — computed as a generic before/after diff so it works for every guide, including third-party ones, without the guide knowing about tracing. This surfaces the shaping decisions that never reach the final prompt (which tools a selector dropped, whether memory retrieval returned anything). `LogGuideContribution` is a default-interface no-op for back-compat; `ConsoleTracer`, `OpenTelemetryTracer`, and `CompositeTracer` implement it. Every trace event carries a zero-based `turn` index (`LogModelCall`, `LogToolCall`, `LogSensorResult`, `LogGuideContribution`) so a backend can group all of a turn's events together: the loop threads its own turn counter into the model/tool/sensor events, and `DefaultGuideRunner` derives the same index from the count of `ModelCallStep`s in the trajectory (the current turn's model call isn't appended until after guides run, so the count *is* the turn index). Adding the parameter to the three non-default `ITracer` methods is a breaking change for external tracer implementations.

`MemoryGuide` retrieves against the **latest** `UserMessageStep` (falling back to `TaskText` when the trajectory has none), not the frozen `TaskText`. The store does relevance ranking, so the query must describe what the agent is working on *now* — in multi-turn chat that's the current question, not the opener. This is unconditional, not a chat seam: a single-task run only ever has one `UserMessageStep` and its content *is* `TaskText` (nothing else appends one during an autonomous run), so task-mode behaviour is unchanged and no mode flag is needed.

### Sensor pattern
Sensors run **in parallel** at five hookpoints (`PreModelCall`, `PostModelCall`, `PreToolCall`, `PostToolCall`, `PreReturn`). A `SensorResult.Intervene(reason)` appends a `SensorInterventionStep` to the trajectory, which `HeadEvictionTrajectoryGuide` renders as a `[HARNESS OBSERVATION — ...]` assistant-role message on the next turn. `HarnessInstructionsGuide` primes the model in the system prompt to treat these notes as directives — feedforward and feedback working together.

AI-powered sensors that call a model internally should return `SensorResult.PassWithUsage(usage, cost)` or `SensorResult.InterveneWithUsage(reason, usage, cost)` so the harness can accumulate the spend on `AgentState.SensorUsage` / `AgentState.SensorCost`. `DefaultBudgetEnforcer` includes these totals in its `MaxCost` and `MaxContextTokens` checks.

Sensors may block actions but must never take turns away from the model — the model always gets the next call so it can self-correct. The loop decides what each intervention means per hookpoint:
- **PreModelCall**: **annotates** — injects the note into the trajectory then proceeds with the model call on the same turn; use for conditional pre-reasoning guidance (e.g. goal-drift warnings, error-streak alerts)
- **PostModelCall**: **rejects** — response is suppressed from trajectory so the model cannot re-see flagged content (e.g. PII); model gets a fresh turn to produce a clean response
- **PreReturn**: **challenges** — answer is not accepted; model gets a fresh turn with its prior answer visible so it can self-correct
- **PreToolCall**: **blocks** — tool is never dispatched; a `ToolCallStep` with `IsError = true` is recorded so the model sees a clean error and can replan
- **PostToolCall**: **flags** — advisory only; the tool has already run and its result is in the trajectory; intervention annotates it but cannot prevent the model from reasoning on it

### State
`AgentState` is an immutable record. Every turn produces a new state via `with`-expressions. The trajectory (`IReadOnlyList<Step>`) is the append-only log of `ModelCallStep`, `ToolCallStep`, and `SensorInterventionStep`.

## Key ports

Defaults differ by entry point: `AddModelHarness` (bare, `Framework`) wires core + no-op defaults; `AddStandardModelHarness` (`Infrastructure`) calls `AddModelHarness` then overrides a few seams and adds the default tools/sensors. **Neither registers a model client — the caller always supplies one** via `.WithModel(...)` / `.WithResilientModel(...)`. Port defaults use `TryAdd` (a matching `.WithX(...)` replaces them); tools, sensors, and guides are additive.

`AddChatHarness` (`Framework`) is a third entry point for multi-turn chat — a thin sibling of `AddStandardModelHarness`, not a fork. It calls `AddModelHarness` then swaps two seams: `TurnScopedBudgetEnforcer` (counts turns/cost/tokens since the last `UserMessageStep`, so each user turn gets a fresh allowance instead of the whole conversation exhausting one budget) and `HeadEvictionTrajectoryGuide(pinOriginalGoal: false)` (drops the `[ORIGINAL GOAL]` pin, since chat's live goal is the latest user turn). No task-completion sensors are wired. Same `HarnessLoop`, `AgentState`, and `Agent`; continue a conversation with `AgentState.WithUserMessage`. Sample: `samples/Conversation`. (Multi-turn memory retrieval needs no chat seam — `MemoryGuide` already queries the latest user turn unconditionally; see the Guide pattern above.)

`AddStandardChatHarness` (`Infrastructure`) is the opinionated sibling — it calls `AddChatHarness` then adds chat-appropriate standard defaults: `InMemoryToolRegistry`, `GetDateTimeTool`, OpenTelemetry tracing, and the `PromptInjectionSensor` + `StuckDetector` security/loop sensors. It deliberately omits the task-completion `ProgressCheckSensor`. `PromptInjectionSensor` scans both inbound tool results (PostToolCall) and the latest user message (PreModelCall) — the latter checks the current chat turn, not just the opener. Sample: `samples/ChatSubAgent`.

| Port | Interface | `AddModelHarness` (bare) | `AddStandardModelHarness` |
|---|---|---|---|
| Model transport | `IModelClient` | none — caller supplies | none — caller supplies |
| Tool registry | `IToolRegistry` | `NullToolRegistry` (empty) | `InMemoryToolRegistry` |
| Memory retrieval | `IMemoryStore` | `NullMemoryStore` (no-op) | ↑ same |
| Skill storage | `ISkillStore` | `NullSkillStore` (no-op) | ↑ same |
| Tool filtering | `IToolSelector` | `PassthroughToolSelector` | ↑ same |
| Trajectory compaction | `ICompactionStrategy` | `NullCompactionStrategy` (omission note); opt in to `AiCompactionStrategy` via `WithAiCompaction(modelClient)` | ↑ same |
| Budget enforcement | `IBudgetEnforcer` | `DefaultBudgetEnforcer` | ↑ same |
| Rate limiting | `IRateLimiter` | `NullRateLimiter` | ↑ same |
| Checkpoint store | `ICheckpointStore` | `NullCheckpointStore` | ↑ same |
| Human notifier | `IHumanNotifier` | `NullHumanNotifier` | ↑ same |
| Clock | `TimeProvider` | `TimeProvider.System` | ↑ same |
| Tracing | `ITracer` | `NullTracer` | `OpenTelemetryTracer` |
| Sensors (additive) | `ISensor` | none | `StuckDetector`, `ProgressCheckSensor`, `PromptInjectionSensor` |
| Tools (additive) | `ITool` | none | `GetDateTimeTool` |

The guide pipeline, `DefaultGuideRunner`, `DefaultSensorRunner`, `DefaultContextBuilder`, and `HeadEvictionTrajectoryGuide` are wired identically by both.

`TimeProvider` is registered as a singleton (`TimeProvider.System`) so every DI-resolved time-dependent component — `Agent`, `HarnessLoop`, the budget enforcers, the rate limiters, `ConsoleTracer`, `FileSkillStore` — reads one injectable clock; override it with standard DI (`services.Replace(...)`) rather than a dedicated builder method. Those components accept the clock by constructor injection (an optional `TimeProvider? = null` falling back to `TimeProvider.System`, except `Agent` which requires it), so direct construction still works without a container. `AgentState` is deliberately the exception: it stays a pure value type, so `NewTask` / `WithUserMessage` take a `DateTimeOffset timestamp` supplied by the caller (`Agent` passes `timeProvider.GetUtcNow()`) rather than depending on a clock at all.

## DI conventions

- DI registration files are named `DependencyInjection.cs`, class `DependencyInjection`, at the **project root** (not in a subfolder).
- Two-method pattern per abstraction: `AddXxx<T>()` (public, explicit override via `Replace`) and `AddXxxDefault()` (private, `TryAdd` — called only by `AddModelHarness`).
- Guides are a collection — `AddXxxGuideDefault()` uses `AddGuide<T>()` internally; opt-out by not calling the default.
- `AddModelHarness(systemPrompt)` is the single public aggregate entry point.

## Coding conventions

- SOLID principles and Clean / Onion Architecture with ports and adapters throughout.
- Primary constructors preferred.
- Interfaces over concrete implementations.
- Small, focused changes — build after each set of changes.
- Remove unused `using` statements in every file touched.
- No comments unless the WHY is non-obvious. XML doc comments belong on the public API surface only (this ships as a NuGet library, so consumers get IntelliSense) — keep them concise. Do not put XML docs on internal/private members; use a plain `//` comment there only where the WHY is non-obvious.
- No unnecessary abstractions — don't design for hypothetical future requirements.

## Workflow

- **Commit and push after every changeset** — do not wait to be asked.
- Before designing a non-trivial solution, ask clarifying questions to improve the spec.
- When using external SDKs or APIs, fetch up-to-date docs rather than relying on training data.
- **After any change, re-check the docs for staleness and fix them in the same changeset** — `README.md`, `CLAUDE.md`, and `docs/*.md` (CONCEPTS, EXTENDING, ROADMAP). If a change renames a symbol, removes a project, alters a default, or adds/removes a feature, update every doc that referenced it so the docs never drift from the code.

## Testing

Unit tests live in `tests/SapphireGuard.ModelHarness.Framework.Tests.Unit`. Run with:

```bash
dotnet test tests/SapphireGuard.ModelHarness.Framework.Tests.Unit/
```

Trivial delegation classes (`SystemPromptGuide`, `NullMemoryStore`, `FakeModelClient`, etc.) carry `[ExcludeFromCodeCoverage]` — no tests needed for them. Everything with conditional logic has tests.

## Roadmap status

See `ROADMAP.md` for the full list. Done: core loop, guide pattern (with compaction), sensor pattern (with production sensors), tools, `AskHumanTool` + `IHumanNotifier` (async HITL suspend/resume), Anthropic adapter, Ollama adapter, Azure OpenAI / AI Foundry adapter, DI composition, context management (memory + tool selection), skills / procedural memory (`ISkillStore` + `SkillsGuide` + skill tools, `ToolCatalogueGuide`), unit tests, OpenTelemetry tracing + metrics via `CompositeTracer`, checkpoint/resume via `Infrastructure.Persistence`. Still to do: (1) Dual-LLM isolation / content quarantine (`IToolResultSanitizer` + `DualLlmToolResultSanitizer`); (2) nested `gen_ai.*` span tree via a span-aware `ITracer` port (flat events → OTel GenAI-aligned nested spans, preserving multi-tracer composition); (3) incremental fold compaction (rolling `AgentState` summary + layered structured clearing, replacing the current re-summarise-everything view). See `ROADMAP.md` for the full write-ups. Additional model providers (OpenAI, Google Gemini) were cut from the backlog — Anthropic, Azure, and Ollama cover current needs; `IModelClient` is the port, add an adapter if and when one is actually needed. Deliberately out of scope (cross-episode concerns that live above the harness): outcome/success evaluation, skill auto-harvest, and the learning/training loop — see `ROADMAP.md`.
