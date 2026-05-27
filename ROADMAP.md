# Roadmap

Features are grouped by theme. Each item links to the port in the codebase
where the implementation would live.

---

### Core loop
- [x] `HarnessLoop` — turn-by-turn orchestration (build context → call model → act on response)
- [x] `IBudgetEnforcer` / `DefaultBudgetEnforcer` — hard limits on turns, tokens, cost, and wall clock
- [x] Budget exhaustion as control flow (not exception) — one final model call, `PartialResult` outcome
- [x] `IRateLimiter` — sits alongside `IBudgetEnforcer` in the pre-model-call check; delays when calls-per-minute or tokens-per-minute thresholds are hit; not a sensor (sensors cannot delay) and not a budget concern (rate limits are provider-enforced windows, not cumulative spend); `RateLimitCheck` result; `WithRateLimiter` DI extension (additive, auto-composed into `CompositeRateLimiter`); `CallsPerMinuteRateLimiter` and `TokensPerMinuteRateLimiter` in Infrastructure; wait exceeding `MaxWallClock` falls through to budget-exhaustion path
- [x] `AgentState` — immutable run-time state; `AppendStep` via `with`-expressions
- [x] `AgentOutcome` — terminal result with status, final answer, and full trajectory

### Guide pattern
- [x] `IGuide` / `IGuideRunner` / `ContextDraft` — sequential pipeline that shapes what the model sees
- [x] `SystemPromptGuide` — injects agent identity and standing instructions
- [x] `HarnessInstructionsGuide` — appends harness conventions to the system prompt; teaches the model to treat `[HARNESS OBSERVATION — ...]` notes as directives (feedforward complement to sensor feedback)
- [x] `TrajectoryGuide` — renders the full trajectory (model turns, tool results, sensor notes) into the prompt; sensor notes use `[HARNESS OBSERVATION — ...]` prefix matching what `HarnessInstructionsGuide` declares
- [x] Token-aware trajectory compaction — `TrajectoryGuide` trims oldest steps when estimated token count approaches `MaxContextTokens`, prepending an omission note when steps are dropped
- [x] Smarter compaction via summarisation — `ICompactionStrategy` port on `TrajectoryGuide`; `NullCompactionStrategy` default (bare omission note, no extra model call); `AiCompactionStrategy` in `Infrastructure` takes any `IModelClient` and produces a 3–5 sentence prose summary of the evicted segment; fails open (falls back to omission note if the model call fails or returns empty); opt in via `builder.WithAiCompaction(modelClient)` — caller supplies the model so the framework stays provider-neutral. Note: compaction calls are not yet surfaced in traces as a separate cost line; can be added to `ITracer` if operators need the breakdown.
- [x] `MemoryGuide` — port for long-term memory; default is `NullMemoryStore` (no-op); replace with a vector store or knowledge graph
- [x] `ToolSelectorGuide` — port for tool filtering/ranking; default is `PassthroughToolSelector` (all tools, unchanged); replace with a relevance-ranking implementation
- [ ] ~~Progressive tool discovery~~ — removed from backlog; see the ADR in README.md (tool relevance ranking row). Short version: a routing layer papers over an agent design problem. An agent with 20+ tools is already a smell; the right fix is decomposition, not a router.
- [x] ReAct loop / goal reiteration — `TrajectoryGuide` re-injects the original `state.TaskText` as a `[ORIGINAL GOAL]` system note on every turn; prevents context drift where the model's working hypothesis gradually diverges from user intent across many turns, especially after compaction drops early history
- [x] Intermediate validation gate — `ProgressCheckSensor` fires at `PreModelCall` every N completed turns (configurable, default 5) and annotates the trajectory with a structured checkpoint prompt; included in `AddStandardModelHarness` by default

### Sensor pattern
- [x] `ISensor` / `ISensorRunner` / `HookPoint` — parallel observation at five lifecycle positions
- [x] `SensorResult.Intervene(reason)` → `SensorInterventionStep` fed back through `TrajectoryGuide`
- [x] `StuckDetector` — built-in sensor; blocks repeated identical tool calls
- [x] `PiiRedactionSensor` — PostModelCall; regex scan for email, phone, credit card, NI, SSN
- [x] `ToolResultSanityCheckSensor` — PostToolCall; validates result shape and per-tool custom rules
- [x] Per-hookpoint intervention semantics: sensors may block actions but must never take turns away from the model. PreModelCall injects a note and proceeds; PostModelCall suppresses content and loops back; PreToolCall blocks dispatch and records an error result; PostToolCall is advisory-only; PreReturn loops back for self-correction
- [x] `PromptInjectionSensor` — PostToolCall; scans inbound tool results for injection patterns (instruction overrides, persona hijacks, role overrides, etc.); flags with an untrusted-content warning; included in `AddStandardModelHarness` by default
- [ ] ~~Irreversible action gate~~ — removed from backlog; the gate only makes sense when a human is available to answer, which an ambient agent cannot guarantee. The ports already exist (`PreToolCall` blocks dispatch; `IHumanChannel` routes questions; checkpoint/resume enables suspension). What counts as irreversible, who gets notified, and how long to wait are system design decisions the harness cannot make — documented in user concerns ADR instead.

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
- [x] `AzureOpenAIModelClient` — Azure AI Foundry / Azure OpenAI Service adapter (`Infrastructure.AzureOpenAI`); supports API key and `DefaultAzureCredential` (managed identity); `WithAzureOpenAIModel` DI extension; `samples/AzureOpenAI` demo
- [ ] ~~Additional model provider adapters (OpenAI, Google Gemini)~~ — removed from backlog; Anthropic, Azure AI Foundry, and Ollama cover current needs. `IModelClient` is the port — add an adapter if and when a new provider is needed.

### DI / composition
- [x] `AddModelHarness(systemPrompt)` — aggregate registration with `TryAdd`/`Replace` discipline
- [x] Two-method pattern per abstraction: `AddXxx<T>()` (explicit override) + `AddXxxDefault()` (TryAdd)
- [x] Graceful fallback to `FakeModelClient` when no API key is configured
- [x] Multi-agent support — `AgentFactory` builds an isolated `ServiceProvider` per named agent (fresh `ServiceCollection`, no shared services); `AgentTool` exposes any named agent as an `ITool` so orchestrators delegate via the standard tool primitive; `AddAgentFactory` + `AddStandardAgent` / `AddAgent` DI extensions in Infrastructure; `AddSubAgentAsTool` builder extension wires sub-agents without shared services; `samples/SubAgent` scripted no-API-key demo

### Persistence
- [x] `ICheckpointStore` / `Checkpoint` / `NullCheckpointStore` — port in `Framework.Persistence`; `HarnessLoop` auto-saves at the top of every turn (captures the fully-completed prior turn)
- [x] `FileCheckpointStore` — writes `{dir}/{taskId}/{timestamp}_{id}.json`; lexicographic filename order makes `LoadLatestAsync` a trivial sort
- [x] `StepJsonConverter` — custom `JsonConverter<Step>` using a `$type` discriminator; handles the polymorphic `Step` hierarchy without annotating the domain model
- [x] `AddFileCheckpointStore(directory)` DI extension; `AddCheckpointStore<T>()` / factory override for custom backends
- [x] At-least-once resume semantics — pass a loaded checkpoint's state (with `Status = Running`) back to `HarnessLoop.RunAsync`

### Human-in-the-loop
- [x] `IHumanNotifier` — port in `Framework.Tools`; one method: `NotifyAsync(HumanInputRequest, ct)` — fire-and-forget; implementation posts HTTP, publishes a bus message, sends Slack DM etc.
- [x] `HumanInputRequest` — carries `TaskId`, `CallId`, and `Question` across the suspension boundary
- [x] `AskHumanTool` — standard `ITool` the model invokes; fires the notifier and returns `ToolResult { IsPending = true }` to signal suspension
- [x] `ToolResult.IsPending` — flag detected by `HarnessLoop` after tool dispatch; triggers checkpoint save and `AwaitingHuman` return
- [x] `AgentOutcome.PendingHumanInput` — carries `CallId` + `Question` when status is `AwaitingHuman`
- [x] `AgentState.ResumeWithHumanAnswer(callId, answer)` — replaces the pending `ToolCallStep` in the trajectory with the real answer; pass result back to `RunAsync` to continue the run
- [x] `ConsoleHumanChannel` — dev-time `IHumanNotifier`; prints the question and returns immediately; the calling loop reads stdin after `RunAsync` suspends
- [x] `AddAskHumanTool<TNotifier>()` / factory overload DI extension in `Infrastructure`
- [x] Decision: HITL is a **system design concern**, not a harness concern — the harness provides the port (`IHumanNotifier`) and suspends with `AwaitingHuman`; how the question is dispatched and the answer routed back are entirely the user's concern
- [x] `samples/HitlSuspendResume` — scripted no-API-key demo of the full suspend/resume cycle

### Skills (procedural memory)
- [x] `ISkillStore` / `Skill` / `SkillSummary` / `NullSkillStore` — port in `Framework.Skills`; default is a no-op so the read side ships on with zero overhead
- [x] `SkillsGuide` — surfaces the skill catalogue (name + when-to-use) into context via progressive disclosure; emits nothing when no skills exist
- [x] `ToolCatalogueGuide` — tool-catalogue rendering extracted out of `DefaultContextBuilder` into a guide via `ContextDraft.SystemSections`; all system-prompt sections are now guide-driven (the builder just concatenates)
- [x] `FileSkillStore` — persists skills as `SKILL.md` (frontmatter + markdown body, minimal hand-rolled parser, no YAML dependency); `AddFileSkillStore(dir)`
- [x] `SkillManageTool` (`skill_manage`) — model-initiated save/delete of procedural memory; `SkillViewTool` (`skill_view`) — loads a full skill body on demand; `AddSkillTools()`
- [x] `samples/SkillLearning` — scripted, no-API-key demo: run 1 captures a skill, run 2 loads it from disk via `SkillsGuide` and reuses it
- [x] User-defined skills — `CompositeSkillStore` aggregates an agent store (writable) and one or more user stores (read-only); agent version shadows a same-named user skill and reveals it again on delete; `AddAgentSkillStore(dir)` + `AddUserSkillStore(dir)` DI helpers; transparent to all consumers (`ISkillStore`, `SkillManageTool`, `SkillViewTool`, `SkillsGuide` unchanged)

### Robustness
- [x] Sensor intervention guard — `HarnessLoop` tracks consecutive sensor blocks; after 3 consecutive `PostModelCall` or `PreReturn` interventions the loop force-finalises with a clear reason rather than looping indefinitely
- [x] Exception telemetry on tool failure — `ExecuteToolAsync` now catches exceptions from `toolRegistry.DispatchAsync`, converts them to `IsError` `ToolResult`s (type + message surfaced to the model), and logs via `tracer.LogToolCall`; tool crashes become recoverable errors rather than run-terminating exceptions
- [ ] ~~Memory retrieval signal (`IMemoryQueryBuilder`)~~ — removed from backlog; the problem (stale query signal on long runs) is better solved by keeping runs focused and bounded via the budget, not by adding a retrieval port. `MemoryGuide` passes `state.TaskText` — sufficient for well-scoped agents.
- [x] DI smoke tests — builder methods and `DependencyInjection.cs` files are `[ExcludeFromCodeCoverage]`; smoke tests live in `tests/.../Smoke/DiSmokeTests.cs`; resolve the container, run one turn against `FakeModelClient` to catch wiring regressions without testing implementation detail

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
  has **no causal role in the loop** — unlike every port the loop does invoke
  (`IMemoryStore`, `ISkillStore`, `IHumanChannel`, `IModelClient`), each of which feeds
  the next turn. Judging belongs to whatever consumes `AgentOutcome`. (Runtime quality
  checks that *do* affect the run are already a `PreReturn` sensor.)
- **Skill auto-harvest** — saving a skill automatically after a successful run depends
  on that success signal and *reacts* to it; both live in the external layer. In the
  harness, skill capture stays model-initiated via `skill_manage`.
- **The learning / training loop** — reflection, trajectory filtering, fine-tuning,
  distillation. The harness is the trajectory *producer*; the trainer is separate.
