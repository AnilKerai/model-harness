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
- [ ] Progressive tool discovery — expose tools contextually rather than all-at-once; research shows selection accuracy degrades meaningfully above ~20 tools; candidate design: a routing step at the start of each turn that classifies the current sub-task and injects only the relevant tool subset into `ContextDraft`; `IToolSelector` is the right seam, needs a smarter default
- [ ] ReAct loop / goal reiteration — implement Focused ReAct: re-inject the original `state.TaskText` as a framing note on every turn (especially after trajectory compaction); prevents context drift where the model's working hypothesis gradually diverges from user intent across many turns; a one-line addition to `TrajectoryGuide` after it emits the omission note

### Sensor pattern
- [x] `ISensor` / `ISensorRunner` / `HookPoint` — parallel observation at five lifecycle positions
- [x] `SensorResult.Intervene(reason)` → `SensorInterventionStep` fed back through `TrajectoryGuide`
- [x] `StuckDetector` — built-in sensor; blocks repeated identical tool calls
- [x] `PiiRedactionSensor` — PostModelCall; regex scan for email, phone, credit card, NI, SSN
- [x] `CostThrottleSensor` — PreModelCall; soft spend cap with force-finalise on trigger
- [x] `ToolResultSanityCheckSensor` — PostToolCall; validates result shape and per-tool custom rules
- [x] Per-hookpoint intervention semantics clarified and documented (PostModelCall suppresses blocked content; PostToolCall is advisory-only; PreModelCall force-finalises)
- [ ] `PromptInjectionSensor` — PostToolCall; scans inbound tool results and retrieved content for injection patterns before they land in trajectory; advisory-only (PostToolCall cannot undo execution) but annotates the result so the model is warned of untrusted content

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
- [ ] Multi-agent support — the current DI model is one agent per container; the entire harness sub-graph (sensors, guides, model client, budget enforcer) would need to be isolated per agent, not just `Agent` itself; candidate approaches include child/scoped containers per agent, a factory that builds an isolated `ServiceProvider` per agent definition, or a dedicated `AgentDefinition` value object that carries its own configuration and is resolved independently

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

### Skills (procedural memory)
- [x] `ISkillStore` / `Skill` / `SkillSummary` / `NullSkillStore` — seam in `Framework.Skills`; default is a no-op so the read side ships on with zero overhead
- [x] `SkillsGuide` — surfaces the skill catalogue (name + when-to-use) into context via progressive disclosure; emits nothing when no skills exist
- [x] `ToolCatalogueGuide` — tool-catalogue rendering extracted out of `DefaultContextBuilder` into a guide via `ContextDraft.SystemSections`; all system-prompt sections are now guide-driven (the builder just concatenates)
- [x] `FileSkillStore` — persists skills as `SKILL.md` (frontmatter + markdown body, minimal hand-rolled parser, no YAML dependency); `AddFileSkillStore(dir)`
- [x] `SkillManageTool` (`skill_manage`) — model-initiated save/delete of procedural memory; `SkillViewTool` (`skill_view`) — loads a full skill body on demand; `AddSkillTools()`
- [x] `samples/SkillLearning` — scripted, no-API-key demo: run 1 captures a skill, run 2 loads it from disk via `SkillsGuide` and reuses it
- [ ] User-defined skills — `CompositeSkillStore` aggregates an agent store (writable; `SaveAsync`/`DeleteAsync` route here) and one or more user stores (read-only by routing); agent version shadows a same-named user skill and reveals it again on delete; DI helpers `AddAgentSkillStore(dir)` + `AddUserSkillStore(dir)` (chainable, order-independent); no changes to `ISkillStore`, `SkillManageTool`, `SkillViewTool`, or `SkillsGuide` — composite is transparent to all consumers; intended to land as part of the fluent builder work

### Robustness
- [ ] Sensor intervention guard — `HarnessLoop` has no hard limit on consecutive sensor interventions per turn; if a sensor blocks at `PostModelCall` and the model immediately re-produces the same violation, the loop runs indefinitely; add a `maxConsecutiveInterventions` counter and force-finalise with a clear reason if exceeded
- [ ] Exception telemetry on tool failure — tool crashes are caught and surfaced as `AgentOutcome { FailureReason = ex.Message }`, discarding the original exception type and stack; at minimum the exception should be emitted via `ITracer` before being swallowed, so production runs are diagnosable
- [ ] Memory retrieval signal — `MemoryGuide` always queries `IMemoryStore` with `state.TaskText`; on long runs the last few model messages or a recent-trajectory summary are better retrieval signals; expose a pluggable `IMemoryQueryBuilder` seam (default: current behaviour)
- [ ] DI smoke tests — builder methods and `DependencyInjection.cs` files are `[ExcludeFromCodeCoverage]`; a single end-to-end smoke test per sample project (resolve the container, run one turn against `FakeModelClient`) would catch wiring regressions without testing implementation detail

### Testing
- [x] `SapphireGuard.ModelHarness.Framework.Tests.Unit` — 85 unit tests covering `HarnessLoop`, `TrajectoryGuide`, `DefaultBudgetEnforcer`, `DefaultSensorRunner`, `StuckDetector`, `DefaultContextBuilder`, all three production sensors, `InMemoryToolRegistry`, `CalculatorTool`
- [x] `[ExcludeFromCodeCoverage]` applied to trivial delegation classes

---

### Deliberately out of scope — lives above the harness

These are **cross-episode** concerns. A harness runs **one episode**; anything that
spans many runs belongs in a separate layer that consumes the harness as a library —
which it already serves everything needed: the final answer, the full trajectory, and
the mechanical `AgentStatus`.

- **Outcome / success evaluation** (an `IOutcomeEvaluator`-style "was the run correct?"
  judge). Deciding correctness is a domain judgment the harness cannot make. Decisive
  test: a verdict, by design, *cannot influence the run* (record, never react), so it
  has **no causal role in the loop** — unlike every seam the loop does invoke
  (`IMemoryStore`, `ISkillStore`, `IHumanChannel`, `IModelClient`), each of which feeds
  the next turn. Judging belongs to whatever consumes `AgentOutcome`. (Runtime quality
  checks that *do* affect the run are already a `PreReturn` sensor.)
- **Skill auto-harvest** — saving a skill automatically after a successful run depends
  on that success signal and *reacts* to it; both live in the external layer. In the
  harness, skill capture stays model-initiated via `skill_manage`.
- **The learning / training loop** — reflection, trajectory filtering, fine-tuning,
  distillation. The harness is the trajectory *producer*; the trainer is separate.
