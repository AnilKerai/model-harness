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

### Persistence
- [ ] **Checkpoint / resume** — `AgentState` is serialisation-ready (no mutable fields); no
  persistence implementation yet. When this lands it will need `[JsonPolymorphic]` source-gen
  for the `Step` hierarchy.
  _Seam: new project `SapphireGuard.ModelHarness.Infrastructure.Persistence`._

### Model providers
- [ ] **Additional model provider adapters** — only Anthropic is implemented. OpenAI, Azure OpenAI,
  Google Gemini, and local models (Ollama) are natural next targets.
  _Seam: `IModelClient`; new project per provider (e.g. `SapphireGuard.ModelHarness.Infrastructure.OpenAI`)._

### Human-in-the-loop
- [ ] **`AgentStatus.AwaitingHuman`** — the status value is reserved but the loop has no mechanism
  to pause, surface a question to a human, and resume. Requires a suspend/resume protocol on
  `HarnessLoop` and a delivery channel (webhook, queue, etc.).
  _Seam: `HarnessLoop`, new `IHumanChannel` abstraction._

### Observability
- [x] **Structured logging / OpenTelemetry** — `OpenTelemetryTracer` emits spans via
  `System.Diagnostics.ActivitySource` and metrics via `System.Diagnostics.Metrics.Meter`.
  No OTel SDK dependency — wire up exporters in the host.
  _Project: `SapphireGuard.ModelHarness.Infrastructure.Telemetry`._
