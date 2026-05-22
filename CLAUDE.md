# CLAUDE.md — model-harness

## What this is

**SapphireGuard.ModelHarness** is a .NET 10 agent framework that wraps LLMs with structured scaffolding. An *agent = model + harness* — the harness (loop, guides, sensors, budget) is what turns a raw model into a controllable agent.

## Build & run

```bash
dotnet build SapphireGuard.ModelHarness.slnx
dotnet run --project src/SapphireGuard.ModelHarness.SampleAgent
```

Add an API key first:

```json
// src/SapphireGuard.ModelHarness.SampleAgent/appsettings.local.json
{ "Anthropic": { "ApiKey": "sk-ant-..." } }
```

## Solution structure

| Project | Role |
|---|---|
| `Framework` | Abstractions + core loop. Only external dep: `Microsoft.Extensions.DependencyInjection.Abstractions` |
| `Infrastructure` | Concrete adapters: `FakeModelClient`, `PollyResilientModelClient`, `ConsoleTracer`, `InMemoryToolRegistry` |
| `Infrastructure.Anthropic` | Anthropic SDK adapter (`ClaudeModelClient`). Depends on Framework only |
| `SampleAgent` | Composition root — shows how to wire everything via DI |

Dependency direction is strict and unidirectional: SampleAgent → Infrastructure / Infrastructure.Anthropic → Framework.

## Core patterns

### HarnessLoop
Turn-by-turn state machine: check budget → sensors (PreModelCall) → build context via guides → call model → sensors (PostModelCall) → dispatch tools → sensors (Pre/PostToolCall) → repeat. Budget exhaustion is control flow, not an exception — one final model call with tools disabled, then `PartialResult`.

### Guide pattern
Guides run **sequentially** before each model call, each contributing to a shared `ContextDraft`. Built-ins: `SystemPromptGuide`, `TrajectoryGuide` (with token-aware compaction), `MemoryGuide` (queries `IMemoryStore`), `ToolSelectorGuide` (delegates to `IToolSelector`). Add custom guides with `services.AddGuide<T>()`.

### Sensor pattern
Sensors run **in parallel** at five hookpoints (`PreModelCall`, `PostModelCall`, `PreToolCall`, `PostToolCall`, `PreReturn`). A `SensorResult.Block(reason)` appends a `SensorInterventionStep` to the trajectory, which `TrajectoryGuide` renders as a system note on the next turn so the model can re-plan.

### State
`AgentState` is an immutable record. Every turn produces a new state via `with`-expressions. The trajectory (`IReadOnlyList<Step>`) is the append-only log of `ModelCallStep`, `ToolCallStep`, and `SensorInterventionStep`.

## Key extension seams

| Seam | Interface | Default |
|---|---|---|
| Model transport | `IModelClient` | `ClaudeModelClient` (Anthropic) / `FakeModelClient` |
| Tool registry | `IToolRegistry` | `InMemoryToolRegistry` |
| Memory retrieval | `IMemoryStore` | `NullMemoryStore` (no-op) |
| Tool filtering | `IToolSelector` | `PassthroughToolSelector` |
| Budget enforcement | `IBudgetEnforcer` | `DefaultBudgetEnforcer` |
| Tracing | `ITracer` | `ConsoleTracer` |

## DI conventions

- DI registration files are named `DependencyInjection.cs`, class `DependencyInjection`, at the **project root** (not in a subfolder).
- Two-method pattern per abstraction: `AddXxx<T>()` (public, explicit override via `Replace`) and `AddXxxDefault()` (private, `TryAdd` — called only by `AddModelHarness`).
- Guides are a collection — `AddXxxGuideDefault()` uses `AddGuide<T>()` internally; opt-out by not calling the default.
- `AddModelHarness(systemPrompt)` is the single public aggregate entry point.

## Coding conventions

- SOLID principles and Clean / Onion Architecture throughout.
- Primary constructors preferred.
- Interfaces over concrete implementations.
- Small, focused changes — build after each set of changes.
- Remove unused `using` statements in every file touched.
- No comments unless the WHY is non-obvious. No XML doc blocks.
- No unnecessary abstractions — don't design for hypothetical future requirements.

## Workflow

- **Commit and push after every changeset** — do not wait to be asked.
- Before designing a non-trivial solution, ask clarifying questions to improve the spec.
- When using external SDKs or APIs, fetch up-to-date docs rather than relying on training data.

## Roadmap status

See `ROADMAP.md` for the full list. Done: core loop, guide pattern (with compaction), sensor pattern, tools, Anthropic adapter, DI composition, context management (memory + tool selection). Still to do: persistence/checkpoint, sub-agents, MCP integration, additional model providers (OpenAI etc.), human-in-the-loop, OpenTelemetry observability.
