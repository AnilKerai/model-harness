# Roadmap

Features are grouped by theme. Each item links to the seam in the codebase
where the implementation would live.

---

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
- [x] Token-aware trajectory compaction — `TrajectoryGuide` trims oldest steps when estimated token count approaches `MaxContextTokens`, prepending an omission note when steps are dropped
- [x] `MemoryGuide` — seam for long-term memory; default is `NullMemoryStore` (no-op); replace with a vector store or knowledge graph
- [x] `ToolSelectorGuide` — seam for tool filtering/ranking; default is `PassthroughToolSelector` (all tools, unchanged); replace with a relevance-ranking implementation

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
- [x] `AskHumanTool` + `IHumanChannel` — signals HITL to the surrounding system; `ConsoleHumanChannel` for development; replace with a channel suited to the deployment environment

### Infrastructure
- [x] `FakeModelClient` — scripted responses for local development without an API key
- [x] `ResilientModelClientDecorator` — wraps any `IModelClient` with Polly retry (exponential back-off) + circuit breaker; lives in `Infrastructure.Resilience` so the Polly dependency is isolated
- [x] `ConsoleTracer` — streams JSON trace events to stdout
- [x] `OpenTelemetryTracer` — emits spans via `ActivitySource` and metrics via `Meter`; no OTel SDK dependency
- [x] `CompositeTracer` — fans out to multiple `ITracer` instances simultaneously
- [x] `ClaudeModelClient` — Anthropic SDK adapter; handles message alternation, tool result inlining, cost tracking
- [x] `OllamaModelClient` — OllamaSharp v5 adapter; stateful tool-call grouping pass; cost is always zero (local inference)
- [ ] Additional model provider adapters — OpenAI, Azure OpenAI, Google Gemini

### DI / composition
- [x] `AddModelHarness(systemPrompt)` — aggregate registration with `TryAdd`/`Replace` discipline
- [x] Two-method pattern per abstraction: `AddXxx<T>()` (explicit override) + `AddXxxDefault()` (TryAdd)
- [x] Graceful fallback to `FakeModelClient` when no API key is configured
- [ ] Named / keyed agent registrations — support multiple `Agent` instances in one container (e.g. a planner agent and an executor agent), each with its own model client, sensors, and guides; likely via `IKeyedServiceProvider` (keyed DI, .NET 8+)

### Persistence
- [x] `ICheckpointStore` / `Checkpoint` / `NullCheckpointStore` — seam in `Framework.Persistence`; `HarnessLoop` auto-saves at the top of every turn (captures the fully-completed prior turn)
- [x] `FileCheckpointStore` — writes `{dir}/{taskId}/{timestamp}_{id}.json`; lexicographic filename order makes `LoadLatestAsync` a trivial sort
- [x] `StepJsonConverter` — custom `JsonConverter<Step>` using a `$type` discriminator; handles the polymorphic `Step` hierarchy without annotating the domain model
- [x] `AddFileCheckpointStore(directory)` DI extension; `AddCheckpointStore<T>()` / factory override for custom backends
- [x] At-least-once resume semantics — pass a loaded checkpoint's state (with `Status = Running`) back to `HarnessLoop.RunAsync`

### Human-in-the-loop
- [x] `IHumanChannel` — seam in `Framework.Tools`; one method: `AskAsync(question, ct) → string`
- [x] `AskHumanTool` — standard `ITool` the model invokes when it needs human input; delegates to `IHumanChannel`
- [x] `ConsoleHumanChannel` — development-time implementation that blocks on stdin; replace with a channel suited to the deployment environment
- [x] `AddAskHumanTool<TChannel>()` / factory overload DI extension in `Infrastructure`
- [x] Decision: HITL is a **system design concern**, not a harness concern — the harness provides the seam (`IHumanChannel`) and signals intent (`AskHumanTool`); how a human is reached is entirely the user's implementation

### Testing
- [x] `SapphireGuard.ModelHarness.Framework.Tests.Unit` — 85 unit tests covering `HarnessLoop`, `TrajectoryGuide`, `DefaultBudgetEnforcer`, `DefaultSensorRunner`, `StuckDetector`, `DefaultContextBuilder`, all three production sensors, `InMemoryToolRegistry`, `CalculatorTool`
- [x] `[ExcludeFromCodeCoverage]` applied to trivial delegation classes
