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
| `Infrastructure` | Concrete implementations: `FakeModelClient`, `ConsoleTracer`, `OpenTelemetryTracer`, `CompositeTracer`, `InMemoryToolRegistry`. Sensors: `StuckDetector`, `ProgressCheckSensor`, `PiiRedactionSensor`, `ToolResultSanityCheckSensor`, `PromptInjectionSensor`. Harness tools: `AskHumanTool`, `ConsoleHumanChannel`. Skills (procedural memory): `FileSkillStore`, `SkillManageTool`, `SkillViewTool`. Depends on Framework only |
| `Infrastructure.Resilience` | `ResilientModelClientDecorator` — wraps any `IModelClient` with Polly retry + circuit breaker. Depends on Framework + Polly v8 |
| `Infrastructure.Anthropic` | Anthropic SDK adapter (`ClaudeModelClient`). Depends on Framework only |
| `Infrastructure.Mcp` | MCP adapter (`McpTool`, `McpToolFactory`). Depends on Framework + ModelContextProtocol |
| `Infrastructure.Persistence` | Checkpoint/resume (`FileCheckpointStore`, `StepJsonConverter`). Depends on Framework only |
| `Infrastructure.Ollama` | Ollama adapter (`OllamaModelClient`) via OllamaSharp v5. Depends on Framework only |
| `Infrastructure.AzureOpenAI` | Azure AI Foundry / Azure OpenAI Service adapter (`AzureOpenAIModelClient`) via `Azure.AI.OpenAI` v2. Supports API key and `DefaultAzureCredential`. Depends on Framework only |
| `samples/*` | One console project per scenario — composition roots showing how to wire everything via DI |

Dependency direction is strict and unidirectional: samples/* → Infrastructure / Infrastructure.Anthropic / Infrastructure.Mcp / Infrastructure.Ollama / Infrastructure.AzureOpenAI / Infrastructure.Resilience → Framework.

## Core patterns

### HarnessLoop
Turn-by-turn state machine: check budget → sensors (PreModelCall) → build context via guides → call model → sensors (PostModelCall) → dispatch tools → sensors (Pre/PostToolCall) → repeat. Budget exhaustion is control flow, not an exception — one final model call with tools disabled, then `PartialResult`.

### Guide pattern
Guides run **sequentially** before each model call, each contributing to a shared `ContextDraft`. Built-ins: `SystemPromptGuide`, `HarnessInstructionsGuide` (appends harness conventions to system prompt), `TrajectoryGuide` (with token-aware compaction), `MemoryGuide` (queries `IMemoryStore`), `ToolSelectorGuide` (delegates to `IToolSelector`), `ToolCatalogueGuide` (renders the tool catalogue into `ContextDraft.SystemSections`), `SkillsGuide` (renders the skill catalogue from `ISkillStore`). Add custom guides with `services.AddGuide<T>()`.

### Sensor pattern
Sensors run **in parallel** at five hookpoints (`PreModelCall`, `PostModelCall`, `PreToolCall`, `PostToolCall`, `PreReturn`). A `SensorResult.Intervene(reason)` appends a `SensorInterventionStep` to the trajectory, which `TrajectoryGuide` renders as a `[HARNESS OBSERVATION — ...]` assistant-role message on the next turn. `HarnessInstructionsGuide` primes the model in the system prompt to treat these notes as directives — feedforward and feedback working together.

Sensors may block actions but must never take turns away from the model — the model always gets the next call so it can self-correct. The loop decides what each intervention means per hookpoint:
- **PreModelCall**: **annotates** — injects the note into the trajectory then proceeds with the model call on the same turn; use for conditional pre-reasoning guidance (e.g. goal-drift warnings, error-streak alerts)
- **PostModelCall**: **rejects** — response is suppressed from trajectory so the model cannot re-see flagged content (e.g. PII); model gets a fresh turn to produce a clean response
- **PreReturn**: **challenges** — answer is not accepted; model gets a fresh turn with its prior answer visible so it can self-correct
- **PreToolCall**: **blocks** — tool is never dispatched; a `ToolCallStep` with `IsError = true` is recorded so the model sees a clean error and can replan
- **PostToolCall**: **flags** — advisory only; the tool has already run and its result is in the trajectory; intervention annotates it but cannot prevent the model from reasoning on it

### State
`AgentState` is an immutable record. Every turn produces a new state via `with`-expressions. The trajectory (`IReadOnlyList<Step>`) is the append-only log of `ModelCallStep`, `ToolCallStep`, and `SensorInterventionStep`.

## Key ports

| Port | Interface | Default |
|---|---|---|
| Model transport | `IModelClient` | `ClaudeModelClient` (Anthropic) / `FakeModelClient` |
| Tool registry | `IToolRegistry` | `InMemoryToolRegistry` |
| Memory retrieval | `IMemoryStore` | `NullMemoryStore` (no-op) |
| Skill storage | `ISkillStore` | `NullSkillStore` (no-op) |
| Tool filtering | `IToolSelector` | `PassthroughToolSelector` |
| Budget enforcement | `IBudgetEnforcer` | `DefaultBudgetEnforcer` |
| Tracing | `ITracer` | `CompositeTracer(ConsoleTracer, OpenTelemetryTracer)` |

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
- No comments unless the WHY is non-obvious. No XML doc blocks.
- No unnecessary abstractions — don't design for hypothetical future requirements.

## Workflow

- **Commit and push after every changeset** — do not wait to be asked.
- Before designing a non-trivial solution, ask clarifying questions to improve the spec.
- When using external SDKs or APIs, fetch up-to-date docs rather than relying on training data.

## Testing

Unit tests live in `tests/SapphireGuard.ModelHarness.Framework.Tests.Unit`. Run with:

```bash
dotnet test tests/SapphireGuard.ModelHarness.Framework.Tests.Unit/
```

Trivial delegation classes (`SystemPromptGuide`, `NullMemoryStore`, `FakeModelClient`, etc.) carry `[ExcludeFromCodeCoverage]` — no tests needed for them. Everything with conditional logic has tests.

## Roadmap status

See `ROADMAP.md` for the full list. Done: core loop, guide pattern (with compaction), sensor pattern (with production sensors), tools, `AskHumanTool` + `IHumanNotifier` (async HITL suspend/resume), Anthropic adapter, MCP adapter, Ollama adapter, DI composition, context management (memory + tool selection), skills / procedural memory (`ISkillStore` + `SkillsGuide` + skill tools, `ToolCatalogueGuide`), unit tests, OpenTelemetry tracing + metrics via `CompositeTracer`, checkpoint/resume via `Infrastructure.Persistence`. Still to do: additional model providers (OpenAI, Google Gemini). Deliberately out of scope (cross-episode concerns that live above the harness): outcome/success evaluation, skill auto-harvest, and the learning/training loop — see `ROADMAP.md`.
