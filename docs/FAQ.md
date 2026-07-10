# FAQ

## Why is budget enforcement a separate code path rather than a `PreModelCall` sensor?

Three reasons, each independent:

**1. Different outcome semantics.** Budget exhaustion returns `AgentOutcome { Status = PartialResult }`, signalling the agent was cut off before it could finish. A sensor block at `PreModelCall` returns `Done`, signalling the agent reached a stopping point. Those mean different things to a caller — `PartialResult` is "we ran out of resources", `Done` is "a policy said stop here and the model wrapped up cleanly". If budget enforcement went through the sensor path, both cases would return `Done` and callers would lose the ability to distinguish them.

**2. Ordering — budget is checked before sensors run.** The loop does `budget check → sensors`, in that order. If budget enforcement were a sensor it would run in parallel with other sensors, which could do work (memory queries, validation calls) before the budget check fires. When the budget is already gone, running sensors at all is wasteful. The explicit check short-circuits everything before any sensor touches the state.

**3. Unconditional vs. pluggable.** Sensors are opt-in — you compose them per agent, and a minimal agent can register none. The budget enforcer is always wired in by `AddModelHarness`; it is a framework guarantee, not a composition choice. If cost throttling were the only enforcement mechanism, a misconfigured agent with no sensors would have no hard ceiling.

## If ordering matters, shouldn't cost throttling and rate limiting also be explicit checks rather than sensors?

The ordering argument is strong against making budget enforcement a sensor; it is much weaker the other way around. Cost throttle and rate limit sensors are typically the cheapest operations in the `PreModelCall` batch (summing decimals from the trajectory) — there is no meaningful wasted work before they fire.

More importantly, pulling them into the explicit sequential path would require the loop to know about them by name, and every agent would get the same thresholds. Keeping them as sensors means each agent can register its own configuration — a background summarisation agent might tolerate a $0.50 soft cap, while a customer-facing agent runs on $0.05. You can also stack multiple policies (a per-task cost cap alongside a per-minute rate limit) without the loop knowing anything about either. First-class checks cannot do this cleanly.

The asymmetry is intentional: budget enforcer → first-class because it is an unconditional framework guarantee with a fixed shape; cost throttle / rate limit → sensors because they are optional, per-agent policies that vary in threshold, number, and kind.

## Why is human-in-the-loop a user concern rather than a harness concern?

Because HITL is a **system design concern**, not a **model control concern** — and the harness only owns the latter.

The harness controls the model: it shapes what the model perceives (guides), observes and constrains what it does (sensors), enforces resource limits (budget), and records everything (trajectory). That is its complete job.

HITL is about what happens *outside* the model loop: how a question is surfaced to a human, over what channel, how long you wait, and how the response is routed back. A CLI tool, a Slack bot, a web app, and an async queue-based pipeline all have fundamentally different answers. The harness cannot make that decision without becoming an application framework.

The boundary: the harness provides `AskHumanTool` backed by `IHumanNotifier`. When the model decides it needs human input, it calls the tool. What `IHumanNotifier.NotifyAsync` does — block on stdin, post to Slack, write to a queue and suspend — is entirely the user's implementation. The harness ships `ConsoleHumanChannel` for development, the same way it ships `FakeModelClient`: useful locally, not a production prescription.

## Why not make `ask_human` and `give_up` mutually exclusive to prevent the model picking the wrong one?

(`ask_human` ships; `give_up` here is a hypothetical companion — a tool an operator might register so the model can abandon a task cleanly. The harness ships no such tool, but the design question generalises to any pair like it.)

The tempting solution is to make them exclusive by registration — agents with a human channel get `ask_human`, agents without one get `give_up`, so the model can never confuse them. This works for the scenario that motivates it, but it over-constrains the design.

A well-designed agent might legitimately want both. An orchestrator could abandon some failed sub-tasks outright while escalating others to a human — depending on severity, cost, or domain logic that only the operator understands. Making the tools mutually exclusive forces the operator to choose one mode for the entire agent, which is a policy decision the harness has no business making.

The actual problem is a **system prompt concern, not a structural one**. The harness cannot reason about whether this particular situation warrants escalation versus abandonment — only the operator can draw that line, and the right place to encode it is in the agent's instructions. For example:

> *"If a privileged action is blocked and you have enough to return a partial answer, call `give_up` with what you have. If you need a human decision to make meaningful progress, call `ask_human` with full context."*

The distinction the operator is drawing is: *do you have something useful to return, or do you need input to continue?* That is domain-specific; the harness should not decide it.

The structural contribution the harness can make is limited but real: tool signatures that force the model to articulate its reasoning (the `question` on `ask_human`, or a `reason` argument on a hypothetical `give_up`) help the model self-select correctly by making it form the argument before committing to the call. Beyond that, reliable selection is an empirical question validated per deployment through testing, not a constraint the framework can enforce.

## Should a harness even have a skills system — isn't "learning" out of scope?

The harness doesn't do the learning. It just provides a few simple building blocks,
and the model uses them.

Here's the easiest way to think about it. The harness handles **one task at a time**.
"Getting better over time" happens **across many tasks**. Those are two different
jobs, so we keep them separate — the harness does the first, and anything that spans
many runs is built on top of it.

All the harness adds is three small, ordinary things:

- a place to store skills (`ISkillStore`),
- a tool the model can call to save or load a skill (`skill_manage` / `skill_view`),
- a guide that lists the saved skills so the model knows they exist (`SkillsGuide`).

None of these are special "learning" machinery. A store is just storage; the tools
are just the model reading and writing notes; the guide just shows a list. The
harness never decides *when* to save a skill or whether the agent is "improving" —
the model makes those calls, and any smarter logic (like automatically saving a
skill after a success) lives in your own code, not in `HarnessLoop`.
