# Roadmap

Features are grouped by theme. Each item links to the seam in the codebase
where the implementation would live.

---

## ✅ Done

### Core loop
- [x] `HarnessLoop` — turn-by-turn orchestration (build context → call model → act on response)
- [x] `IBudgetEnforcer` / `DefaultBudgetEnforcer` — hard limits on turns, tokens, cost, and wall clock
- [x] Budget exhaustion as control flow (not exception) — one final model call, `PartialResult` outcome
- [x] `AgentState` — immutable run-time state; `AppendStep` via `with`-expressions
- [x] `AgentOutcome` — terminal result with status, final answer, and full trajectory

### Guide pattern
- [x] `IGuide` / `IGuideRunner` / `ContextDraft` — sequential pipeline that shapes what the model sees
- [x] `SystemPromptGuide` — injects agent identity and standing instructions
- [x] `HarnessInstructionsGuide` — appends harness conventions to the system prompt; teaches the model to treat `[HARNESS OBSERVATION — ...]` notes as directives (feedforward complement to sensor feedback)
- [x] `TrajectoryGuide` — renders the full trajectory (model turns, tool results, sensor notes) into the prompt; sensor notes use `[HARNESS OBSERVATION — ...]` prefix matching what `HarnessInstructionsGuide` declares
- [x] `MemoryGuide` — seam for long-term memory; currently a no-op
- [x] `ToolSelectorGuide` — seam for tool filtering/ranking; currently a no-op

### Sensor pattern
- [x] `ISensor` / `ISensorRunner` / `HookPoint` — parallel observation at five lifecycle positions
- [x] `SensorResult.Intervene(reason)` → `SensorInterventionStep` fed back through `TrajectoryGuide`
- [x] `StuckDetector` — built-in sensor; blocks repeated identical tool calls
- [x] `PiiRedactionSensor` — PostModelCall; regex scan for email, phone, credit card, NI, SSN
- [x] `CostThrottleSensor` — PreModelCall; soft spend cap with force-finalise on trigger
- [x] `ToolResultSanityCheckSensor` — PostToolCall; validates result shape and per-tool custom rules
- [x] Per-hookpoint intervention semantics clarified and documented (PostModelCall suppresses blocked content; PostToolCall is advisory-only; PreModelCall force-finalises)

### Tools
- [x] `ITool` / `IToolRegistry` / `ToolDefinition` — tool abstraction decoupled from `IModelClient`
- [x] `InMemoryToolRegistry`
- [x] `EchoTool`, `CalculatorTool` — sample tools

### Infrastructure
- [x] `FakeModelClient` — scripted responses for local development without an API key
- [x] `PollyResilientModelClient` — retry (exponential back-off) + circuit breaker decorator
- [x] `ConsoleTracer` — streams JSON trace events to stdout
- [x] `ClaudeModelClient` — Anthropic SDK adapter; handles message alternation, tool result inlining, cost tracking

### DI / composition
- [x] `AddModelHarness(systemPrompt)` — aggregate registration with `TryAdd`/`Replace` discipline
- [x] Two-method pattern per abstraction: `AddXxx<T>()` (explicit override) + `AddXxxDefault()` (TryAdd)
- [x] Graceful fallback to `FakeModelClient` when no API key is configured

### Persistence
- [x] `ICheckpointStore` / `Checkpoint` / `NullCheckpointStore` — seam in `Framework.Persistence`; `HarnessLoop` auto-saves at the top of every turn (captures the fully-completed prior turn)
- [x] `FileCheckpointStore` — writes `{dir}/{taskId}/{timestamp}_{id}.json`; lexicographic filename order makes `LoadLatestAsync` a trivial sort
- [x] `StepJsonConverter` — custom `JsonConverter<Step>` using a `$type` discriminator; handles the polymorphic `Step` hierarchy without annotating the domain model
- [x] `AddFileCheckpointStore(directory)` DI extension; `AddCheckpointStore<T>()` / factory override for custom backends
- [x] At-least-once resume semantics — pass a loaded checkpoint's state (with `Status = Running`) back to `HarnessLoop.RunAsync`

### Testing
- [x] `SapphireGuard.ModelHarness.Framework.Tests.Unit` — 85 unit tests covering `HarnessLoop`, `TrajectoryGuide`, `DefaultBudgetEnforcer`, `DefaultSensorRunner`, `StuckDetector`, `DefaultContextBuilder`, all three production sensors, `InMemoryToolRegistry`, `CalculatorTool`
- [x] `[ExcludeFromCodeCoverage]` applied to trivial delegation classes

---

## 🔲 Not yet implemented

### Context management
- [x] **Token-aware trajectory compaction** — `TrajectoryGuide` trims oldest steps when estimated
  token count approaches `MaxContextTokens`, prepending an omission note when steps are dropped.
  Token budget is `MaxContextTokens - reservedTokens` (default 2000 for system prompt + output headroom).

- [x] **Long-term memory** — `IMemoryStore` seam; `MemoryGuide` queries it with the task text and
  surfaces snippets into `ContextDraft.MemorySnippets`. Default is `NullMemoryStore` (no-op).
  Replace with a vector store or knowledge graph implementation.

- [x] **Tool relevance ranking** — `IToolSelector` seam; `ToolSelectorGuide` delegates to it to
  filter or rerank `ContextDraft.AvailableTools` per turn. Default is `PassthroughToolSelector`
  (all tools, unchanged). Replace with a relevance-ranking implementation.

### Model providers
- [x] **Ollama adapter** — `OllamaModelClient` via OllamaSharp v5; handles Ollama's tool call
  grouping requirement (all calls from one model turn in a single assistant message) with a
  stateful flush pass over the trajectory. Cost is always zero (local inference).
  _Lives in `SapphireGuard.ModelHarness.Infrastructure.Ollama`._
- [ ] **Additional model provider adapters** — OpenAI, Azure OpenAI, Google Gemini.
  _Seam: `IModelClient`; new project per provider (e.g. `SapphireGuard.ModelHarness.Infrastructure.OpenAI`)._

### Human-in-the-loop
- [ ] **`AgentStatus.AwaitingHuman`** — the status value is reserved but the loop has no mechanism
  to pause, surface a question to a human, and resume. Requires a suspend/resume protocol on
  `HarnessLoop` and a delivery channel (webhook, queue, etc.).
  _Seam: `HarnessLoop`, new `IHumanChannel` abstraction._

### Observability
- [x] **Structured logging / OpenTelemetry** — `OpenTelemetryTracer` emits spans via
  `System.Diagnostics.ActivitySource` and metrics via `System.Diagnostics.Metrics.Meter`.
  No OTel SDK dependency — wire up exporters in the host. `CompositeTracer` fans out to
  multiple `ITracer` instances simultaneously.
  _Lives in `SapphireGuard.ModelHarness.Infrastructure` alongside `ConsoleTracer`._
