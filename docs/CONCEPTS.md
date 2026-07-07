# Concepts

Background on the agentic-AI ideas this framework implements — **loop engineering**, **context engineering**, and the **agentic primitives**. The [README](../README.md) covers the framework itself (patterns, ports, wiring); this guide covers the *why* behind it.

These are teaching notes. Where a framing is the wider field's standard, it is called out as such; where it is this project's own lens, that is flagged too — so you can tell "industry standard" from "how we cut it here".

- [Loop engineering](#loop-engineering)
- [Context engineering](#context-engineering)
- [Agentic primitives](#agentic-primitives)
- [Prompt injection and taint tracking](#prompt-injection-and-taint-tracking)

---

## Loop engineering

*Prompt → context → loop* is the progression of where the engineering effort goes, each rung wrapping the one below rather than replacing it. Loop engineering — the rung that crystallised in mid-2026 — is the shift from prompting an agent turn-by-turn to **designing the system that prompts it**. The model is the **brain**; the harness is the **body** that equips a single run; the **loop** is the driver that runs the body across many runs — discovering work, triggering on a schedule or event, spawning helpers, verifying results, persisting state, and deciding what to do next until a goal is met.

**This framework is the body.** It is built to be *driven* by a loop, ships the inner loop and the building blocks a loop is assembled from, and deliberately leaves the outer loops above it. (So `HarnessLoop` — the framework's inner control loop — is *not* the outer "loop" the discipline is named for.)

### The four loops

Loop engineering stacks feedback loops from the single run outward; each layer wraps the one inside it, and the further out, the longer its period:

| Loop | What it does | In this harness |
|---|---|---|
| **1. Agent loop** | Model calls tools in a loop until the task is done | ✅ `HarnessLoop` — the turn-by-turn state machine |
| **2. Verification loop** | Check each result; feed failures back so the agent self-corrects | ◐ inline as **sensors** (`PreReturn` challenge, `PostToolCall` flag); offline as **evals** in a sibling eval project |
| **3. Event-driven loop** | Start runs from cron / webhooks / events, not by hand | ⛔ above the harness — the host composes a scheduler around `Agent` |
| **4. Hill-climbing loop** | Read traces + outcomes, then update the agent's own config to do better next time | ⛔ above the harness — the harness only *emits* the traces (`ITracer`) it would consume; see [ROADMAP](ROADMAP.md) |

The kit a loop assembles from — skills, sub-agents, connectors, persistence, a human gate — is the [agentic primitives](#agentic-primitives) below, all provided by the framework as in-run features. The two a loop adds on top are robust **termination** and **verification**. The harness even ships the *mutation primitive* a hill-climbing loop would use — `skill_manage` lets the agent persist reusable skills ([Agent Learning](../README.md#agent-learning-experimental)) — but in-episode and model-driven; the outcome-scored controller that decides *when* to write is the part left above.

### Termination is the part naive loops get wrong

The signature bug of a hand-rolled loop is that it never stops — the failure the trend nicknamed *loopmaxxing* (assuming more iterations will eventually solve it). A robust loop carries several **independent** exits, and this is where the harness is deliberately strong:

- **Budget as control flow** — turns, tokens, cost, and wall-clock are hard limits checked at the top of every turn; exhaustion returns a `PartialResult`, never an exception. A loop without a budget has no stopping condition, only a failure mode.
- **No-progress detection** — `StuckDetector` and the loop sensors (`MonologueLoopSensor`, `AlternatingToolLoopSensor`, `ToolErrorLoopSensor`) catch the subtle exit: turns being spent without advancing.
- **Verification** — a `PreReturn` sensor can refuse an answer and hand the model a fresh turn to fix it.

Deterministic exits beat asking a model "are we done?" — a budget can't be talked out of stopping.

### How it relates to harness and eval engineering

Three disciplines, three tiers, one stack — loop engineering *wraps* the others, it does not replace them:

| Discipline | Owns | Period | Here |
|---|---|---|---|
| **Harness engineering** | one reliable run — loop, guides, sensors, budget | a single episode | this repo |
| **Eval engineering** | measuring runs against criteria | offline, per release | a sibling eval project |
| **Loop engineering** | driving the harness across runs; at the top, improving it | many episodes | composed above |

The division of labour is clean: the harness **runs** an episode, the eval **measures** it, the loop **acts on the measurement**. The verification loop *consumes* eval engineering; the hill-climbing loop consumes both the harness's traces and the eval's verdicts to decide what to change. Only that outermost act — changing the agent based on measured outcomes — is out of scope here, by design.

This is why **verifier** is worth splitting across tiers: an **eval** runs offline over a fixed dataset and gates a release; a **runtime guardrail** runs inline during one run and feeds a failure back so the model self-corrects before returning. Same predicate, different time and consequence — offline it yields a score, inline it yields a retry.

> The four-loop framing here follows [LangChain's "The Art of Loop Engineering"](https://www.langchain.com/blog/the-art-of-loop-engineering). The term crystallised around mid-2026, so treat it as the current influential framing rather than a settled standard.

---

## Context engineering

Context engineering — as I understand it — is the practice of shaping model perception: deciding what information goes into the context window at each step, what gets left out, and how it's structured. The guide pattern in this framework is a concrete implementation of that idea.

The four standard operations — **Write, Select, Compress, Isolate** — were named by [LangChain (Lance Martin)](https://www.langchain.com/blog/context-engineering-for-agents); each has a named home here:

| CE operation | What it means | Port |
|---|---|---|
| **Write** | Externalise information outside the window for later retrieval | `ICheckpointStore`, `ISkillStore` |
| **Select** | Pull relevant information into the window when needed | `IMemoryStore`, `IToolSelector`, `ISkillStore` |
| **Compress** | Reduce token count while preserving signal | `ITrajectoryGuide`, `ICompactionStrategy` |
| **Isolate** | Partition context so sub-agents don't pollute each other | `AgentFactory` |

Each port is listed in the [extension points](../README.md#extension-points) section of the README.

The primitives below name the building blocks the harness provides for each of these operations; the [core patterns](../README.md#core-patterns) in the README show how they are implemented.

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
| **Sub-agents** | Agents calling other agents — an orchestrator delegates a sub-task to a specialist, gets a result back, and continues. From the calling agent's perspective a sub-agent is just a tool: it takes a task and returns a result. The isolation contract is what makes this composable — each agent runs in its own container with its own model, sensors, and budget. Implemented in the framework via **`AgentFactory`** and **`AgentTool`** — see [EXTENDING.md](EXTENDING.md). |

A "research agent" is control flow + tools + memory. An "orchestrated pipeline" adds sub-agents. Building the framework around named primitives keeps the ports obvious: when a new concern arrives, there is usually a clear home for it.

> **How this maps to the classic decomposition.** These six are a lens tuned to *this framework's ports*, not the canonical taxonomy. The most-taught framing of agent building blocks ([Lilian Weng, "LLM Powered Autonomous Agents"](https://lilianweng.github.io/posts/2023-06-23-agent/)) is **Planning, Memory, Tools,** and **Reflection** around an LLM core. The mapping: *Tools* and *Memory* match; *Perception* is context construction; *Control flow* is the agent loop; *Guardrails* (sensors) carry the externally-driven half of *Reflection*; and *Planning* lives in the model's own reasoning — primed by `ReActGuide` — rather than as a separate named component. Use the six to navigate the code; use Planning/Memory/Tools/Reflection to connect to the wider literature.

> **On the memory varieties.** The *external / retrieved* bucket above is where the cognitive-science taxonomy (working / episodic / semantic / procedural, popularised for agents by the **CoALA** framework) splits *episodic* memory (what happened) from *semantic* memory (abstracted facts) — this framework treats both as retrieval and does not distinguish them. The *in-weights* variety is what that literature calls **parametric** memory; the other three are **non-parametric** — they live in, or are pulled into, the context window rather than the model's weights.

---

## Prompt injection and taint tracking

Prompt injection is the security threat specific to agentic systems: an LLM cannot reliably distinguish **instructions** (from the operator) from **data** (tool results, web pages, documents), so hostile text embedded in external content can hijack the agent into *acting* — sending email, executing code, exfiltrating data. The threat scales with what the agent can do. The [README](../README.md#prompt-injection-and-taint-tracking-experimental) covers how this framework defends; this is the theory underneath.

**Taint tracking** is the defence, borrowed from systems security:

1. Any data that originates from an untrusted external source is marked **tainted**.
2. Taint propagates forward: any computation that *uses* tainted data produces tainted output.
3. Tainted data is never permitted to flow into a **privileged action** — an operation with real-world side effects.

This is what the [CaMeL framework](https://arxiv.org/abs/2503.18813) (Google DeepMind, 2025) proposes for LLM agents: track the provenance of every value flowing through the system, and gate privileged tool calls on whether their arguments trace back to untrusted sources. The catch is that LLMs are opaque — you cannot instrument the model's reasoning to track which parts of its output derived from which parts of its input — so full CaMeL-style taint tracking is an active research problem. This harness ships a practical approximation, described in the [README](../README.md#prompt-injection-and-taint-tracking-experimental).
