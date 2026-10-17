# Agentic-AI primer

Background on the agentic-AI ideas this framework implements — the **agentic primitives**, **context engineering**, and **loop engineering**. The [README](../README.md) covers the framework itself (patterns, ports, wiring); this guide covers the *why* behind it.

The model is the **brain**; this framework is the **body**. These notes build up to that, each section wrapping the one before: the primitives a body is assembled from, then how it shapes what the model sees, then the loop that drives it.

These are teaching notes. Where a framing is the wider field's standard, it is called out as such; where it is this project's own lens, that is flagged too — so you can tell "industry standard" from "how we cut it here".

- [Agentic primitives](#agentic-primitives)
- [Context engineering](#context-engineering)
- [Loop engineering](#loop-engineering)
- [Prompt injection and taint tracking](#prompt-injection-and-taint-tracking)

---

## Agentic primitives

Every agent, no matter how capable it looks, is an assembly of a small set of building blocks. Understanding these primitives makes it easier to read this code and reason about where a new concern belongs.

| Primitive | What it is |
|---|---|
| **Tools** | The agent's hands — functions it can call to effect change or read state outside the context window. Tools are the only way an agent can act on the world. |
| **Memory** | What the agent can remember. Four varieties: *in-context* (the current context window), *external / retrieved* (a vector store or knowledge graph queried each turn), *procedural* (named instructions the agent can load and follow), and *in-weights* (fine-tuning — lives outside the harness). The *procedural* variety is implemented here via **Agent Learning** — `SkillsGuide` surfaces saved skills into context each turn, and `skill_manage` / `skill_view` let the model write and read them. |
| **Perception** | What the agent can see right now — the shaped view of world state it reasons over before each model call. Every prompt is an act of perception design. Getting this right matters more than most people expect: what you omit is as important as what you include. Implemented in the framework via the **Guide pattern** — a sequential pipeline of guides that each contribute to a shared context draft before every model call. |
| **Control flow** | The loop that decides what runs next and in what order: call model → act on response → repeat. Budget enforcement, rate limiting, and checkpoint/resume are all control-flow concerns. The loop is deceptively simple; almost all agent reliability problems are really control-flow problems in disguise. |
| **Guardrails** | Checkpoints that intercept and shape agent behaviour at declared points in the loop — before the model call, after it, before a tool runs, after it, before the final answer is accepted. Guardrails observe and redirect; they do not take turns away from the model. Implemented in the framework via the **Sensor pattern** — sensors run in parallel at five hookpoints and feed interventions back through the guide pipeline so the model can self-correct. |
| **Sub-agents** | Agents calling other agents — an orchestrator delegates a sub-task to a specialist, gets a result back, and continues. From the calling agent's perspective a sub-agent is just a tool: it takes a task and returns a result. The isolation contract is what makes this composable — each agent runs in its own DI container (a separate, in-process `ServiceProvider` — not an OS container or sandbox) with its own model, sensors, and budget. Implemented in the framework via **`AgentFactory`** and **`AgentTool`** — see [EXTENDING.md](EXTENDING.md). |

A "research agent" is control flow + tools + memory. An "orchestrated pipeline" adds sub-agents. Building the framework around named primitives keeps the ports obvious: when a new concern arrives, there is usually a clear home for it.

> **How this maps to the classic decomposition.** These six are a lens tuned to *this framework's ports*, not the canonical taxonomy. The most-taught framing of agent building blocks ([Lilian Weng, "LLM Powered Autonomous Agents"](https://lilianweng.github.io/posts/2023-06-23-agent/)) is **three** components around an LLM core — **Planning, Memory,** and **Tool Use** — with *self-reflection* a sub-part of Planning rather than a peer. The mapping: *Tools* and *Memory* match (she calls the first *Tool Use*); *Perception* is context construction; *Control flow* is the agent loop; *Guardrails* (sensors) externalise the reflection step her framing keeps inside Planning; and *Planning* itself lives in the model's own reasoning — primed by `ReActGuide` — rather than as a separate named component. Use the six to navigate the code; use Planning/Memory/Tool Use to connect to the wider literature.

> **On the memory varieties.** The *external / retrieved* bucket above is where the cognitive-science taxonomy (working / episodic / semantic / procedural, popularised for agents by the **CoALA** framework) splits *episodic* memory (what happened) from *semantic* memory (abstracted facts) — this framework treats both as retrieval and does not distinguish them. The *in-weights* variety is what that literature calls **parametric** memory; the other three are **non-parametric** — they live in, or are pulled into, the context window rather than the model's weights.

> **On guides and sensors.** The **guide** (feedforward — steer the agent *before* it acts) and **sensor** (feedback — observe and correct it *after*) framing this project is built on, and the term *harness engineering* itself, come from Birgitta Böckeler's ["Harness engineering for coding agent users"](https://martinfowler.com/articles/harness-engineering.html) (martinfowler.com, 2026). This framework applies to autonomous agents a vocabulary she developed for coding agents.

---

## Context engineering

Context engineering — as I understand it — is the practice of shaping model perception: deciding what information goes into the context window at each step, what gets left out, and how it's structured. The guide pattern in this framework is a concrete implementation of that idea.

It is also about **how much**. The window is a finite budget: every token is resent on every call, so context size drives cost and latency, and more context can dilute the model's attention rather than help it. Anthropic's framing is to find ["the smallest possible set of high-signal tokens that maximize the likelihood of some desired outcome"](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents).

The four standard operations — **Write, Select, Compress, Isolate** — were named by [LangChain (Lance Martin)](https://www.langchain.com/blog/context-engineering-for-agents); each has a named home here:

| CE operation | What it means | Port |
|---|---|---|
| **Write** | Save information outside the window so the agent can use it later (scratchpads, notes, memories) | `ISkillStore` (written by the model via `skill_manage`) |
| **Select** | Pull relevant information into the window when needed | `IMemoryStore`, `IToolSelector`, `ISkillStore` |
| **Compress** | Reduce token count while preserving signal | `ITrajectoryGuide`, `ICompactionStrategy` |
| **Isolate** | Partition context so sub-agents don't pollute each other | `AgentFactory` |

`ICheckpointStore` is not Write in this sense: it persists run state so the *host* can resume after a crash or a human pause, but the model never reads it back.

Each port is listed in the [extension points](../README.md#extension-points) section of the README.

The primitives above name the building blocks the harness provides for each of these operations; the [core patterns](../README.md#core-patterns) in the README show how they are implemented.

---

## Loop engineering

*Prompt → context → loop* is the progression of where the engineering effort goes, each rung wrapping the one below rather than replacing it. Loop engineering — the rung that crystallised in mid-2026 — is the shift from prompting an agent turn-by-turn to **designing the system that prompts it**. The model is the **brain**; the harness is the **body** that equips a single run; the **loop** is the driver that runs the body across many runs — discovering work, triggering on a schedule or event, spawning helpers, verifying results, persisting state, and deciding what to do next until a goal is met.

**This framework is the body.** It is built to be *driven* by a loop, ships the inner loop and the building blocks a loop is assembled from, and deliberately leaves the outer loops above it. (So `HarnessLoop` — the framework's inner control loop — is *not* the outer "loop" the discipline is named for.)

### The four loops

Loop engineering stacks feedback loops from the single run outward; each layer wraps the one inside it, and the further out, the longer its period:

| Loop | What it does | In this harness |
|---|---|---|
| **1. Agent loop** | Model calls tools in a loop until the task is done | ✅ `HarnessLoop` — the turn-by-turn state machine |
| **2. Verification loop** | Check each result; feed failures back so the agent self-corrects | ◐ inline as **sensors** — a `PreReturn` sensor is a grader that challenges the answer with feedback (`CriticSensor` is LLM-as-judge; `StructuredOutputSensor` is deterministic); `PostToolCall` only flags. Offline evals are *not* this loop: they score runs without feeding back into them, so their scores feed the hill-climbing loop |
| **3. Event-driven loop** | Start runs from cron / webhooks / events, not by hand | ⛔ above the harness — the host composes a scheduler around `Agent` |
| **4. Hill-climbing loop** | Read traces + outcomes, then update the agent's own config to do better next time | ⛔ above the harness — the harness only *emits* the traces (`ITracer`) it would consume; see [ROADMAP](ROADMAP.md) |

The kit a loop assembles from — skills, sub-agents, connectors, persistence, a human gate — is built from the [agentic primitives](#agentic-primitives) above, all provided by the framework as in-run features. The two a loop adds on top are robust **termination** and **verification**. The harness even ships the *mutation primitive* a hill-climbing loop would use — `skill_manage` lets the agent persist reusable skills ([Agent Learning](FEATURES.md#agent-learning-experimental)) — but in-episode and model-driven; the outcome-scored controller that decides *when* to write is the part left above.

Every loop also has a natural place for a **human**, and the same LangChain article treats that as part of the design rather than a fallback: approve sensitive tool calls in the agent loop (here, `AskHumanTool` + `IHumanNotifier`), act as the grader in the verification loop, approve outputs before they reach the end user in the event-driven loop, and review harness changes before they ship in the hill-climbing loop.

### Termination is the part naive loops get wrong

The signature bug of a hand-rolled loop is that it never stops — the failure the trend nicknamed *loopmaxxing* (assuming more iterations will eventually solve it). A robust loop carries several **independent** exits. Inside one run, this is where the harness is deliberately strong:

- **Budget as control flow** — turns, tokens, cost, and wall-clock are hard limits checked at the top of every turn; exhaustion returns a `PartialResult`, never an exception. A loop without a budget has no stopping condition, only a failure mode.
- **No-progress detection** — `StuckDetector` and the loop sensors (`MonologueLoopSensor`, `AlternatingToolLoopSensor`, `ToolErrorLoopSensor`) catch the subtle exit: turns being spent without advancing.
- **Verification** — a `PreReturn` sensor can refuse an answer and hand the model a fresh turn to fix it; the loop's consecutive-intervention cap bounds how many times.

Those are the inner loop's exits; each outer loop needs its own. The verification loop needs a cap on grader retries. The event-driven loop needs a stop that actually halts the work already running — the stop signal has to reach the inner run as cancellation (here, the `CancellationToken` passed to `RunAsync`), because marking a task stopped in a queue does not stop a worker already executing it — plus a runtime or spend cap per task. The hill-climbing loop needs a gate before a change ships, such as a regression set or human review.

A model can judge whether the work *passes* — that is what an LLM-as-judge grader is — but only a deterministic exit is guaranteed to *stop*: a budget can't be talked out of stopping. Pair every model judge with a hard cap.

### How it relates to harness and eval engineering

Three disciplines, three tiers, one stack — loop engineering *wraps* the others, it does not replace them:

| Discipline | Owns | Period | Here |
|---|---|---|---|
| **Harness engineering** | one reliable run — loop, guides, sensors, budget | a single episode | this repo |
| **Eval engineering** | measuring runs against criteria | offline per release, and online over production traces | above this repo (out of scope) |
| **Loop engineering** | driving the harness across runs; at the top, improving it | many episodes | composed above |

The division of labour is clean: the harness **runs** an episode, the eval **measures** it, the loop **acts on the measurement**. The verification loop *reuses* eval engineering's graders and rubrics inline; the hill-climbing loop consumes both the harness's traces and the eval's verdicts to decide what to change. Only that outermost act — changing the agent based on measured outcomes — is out of scope here, by design.

This is why **verifier** is worth splitting across tiers: an **eval** scores runs — offline over a fixed dataset to gate a release, or online over production traces to monitor quality — without changing them; a **runtime guardrail** runs inline during one run and feeds a failure back so the model self-corrects before returning. Same predicate, different consequence — an eval yields a score, a guardrail yields a retry.

> The four-loop framing here follows [LangChain's "The Art of Loop Engineering"](https://www.langchain.com/blog/the-art-of-loop-engineering). The term crystallised around mid-2026, so treat it as the current influential framing rather than a settled standard.

---

## Prompt injection and taint tracking

Prompt injection is the security threat specific to agentic systems: an LLM cannot reliably distinguish **instructions** (from the operator) from **data** (tool results, web pages, documents), so hostile text embedded in external content can hijack the agent into *acting* — sending email, executing code, exfiltrating data. The threat scales with what the agent can do. The [feature write-up](FEATURES.md#prompt-injection-and-taint-tracking-experimental) covers how this framework defends; this is the theory underneath.

**Taint tracking** is the defence, borrowed from systems security:

1. Any data that originates from an untrusted external source is marked **tainted**.
2. Taint propagates forward: any computation that *uses* tainted data produces tainted output.
3. Tainted data is never permitted to flow into a **privileged action** — an operation with real-world side effects.

This is what the [CaMeL framework](https://arxiv.org/abs/2503.18813) (ETH Zürich & Google DeepMind, 2025) proposes for LLM agents: track the provenance of every value flowing through the system, and gate privileged tool calls on whether their arguments trace back to untrusted sources. The catch is that LLMs are opaque — you cannot instrument the model's reasoning to track which parts of its output derived from which parts of its input. CaMeL sidesteps that with a custom interpreter that tracks provenance *around* the model rather than through it; reproducing that in a free-form agent, without such scaffolding, is an open problem. This harness ships a practical approximation, described in the [feature write-up](FEATURES.md#prompt-injection-and-taint-tracking-experimental).
