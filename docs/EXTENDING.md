# Extending the framework

Code recipes for implementing and registering each extension point. Start with the [extension points map](../README.md#extension-points) to understand what exists before choosing what to implement.

## Customising the harness

Use `AddModelHarness` directly when you want full control over every registered component —
it is the lower-level entry point that `AddStandardModelHarness` builds on:

```csharp
services.AddModelHarness(builder => builder
    .WithSystemPrompt("You are a helpful assistant.")
    .WithConsoleTracer()
    .WithOtelTracer()
    .WithToolRegistry<InMemoryToolRegistry>()
    .WithTool<CalculatorTool>()
    .WithSensor<MySensor>()
    .WithGuide<MyGuide>()
    .WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions { ApiKey = apiKey })));
```

Everything below is opt-in whether you use `AddStandardModelHarness` or `AddModelHarness`.

## Conversational (chat) agents

`AddModelHarness` / `AddStandardModelHarness` run a task to a terminal state — they are built for
"do this, return a result". A **chat agent** has a different lifecycle: it stays open across many
user turns, each with its own goal. Use `AddChatHarness` (bare, `Framework`) or
`AddStandardChatHarness` (opinionated, `Infrastructure`) for that shape.

```csharp
services.AddStandardChatHarness(builder => builder
    .WithSystemPrompt("You are a friendly assistant.")
    .WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions { ApiKey = apiKey })));
```

These are thin siblings of the model-harness entry points — same `HarnessLoop`, `AgentState`, and
`Agent` — that swap two seams for the conversational lifecycle:

- **Per-turn budget** (`TurnScopedBudgetEnforcer`): counts turns/cost/tokens since the last
  `UserMessageStep`, so every user turn gets a fresh allowance. The default `DefaultBudgetEnforcer`
  sums the whole trajectory, which would exhaust a long chat after `MaxTurns` total model calls.
- **Unpinned goal** (`HeadEvictionTrajectoryGuide(pinOriginalGoal: false)`): drops the
  `[ORIGINAL GOAL]` anchor, since a conversation's live goal is the latest user turn, not the opener.

`AddStandardChatHarness` additionally wires the chat-appropriate standard defaults —
`InMemoryToolRegistry`, `GetDateTimeTool`, OpenTelemetry tracing, and the `PromptInjectionSensor`
+ `StuckDetector` security/loop sensors. It deliberately omits the task-completion
`ProgressCheckSensor` (a conversation has no single goal to make progress toward). The bare
`AddChatHarness` wires no sensors or tools — it *can't*, since those live in `Infrastructure` — so
reach for it only when you want a minimal pure-chat loop and will wire your own.

Drive the conversation by carrying state forward with `WithUserMessage` — it re-opens the completed
run and appends the next user turn, so the model sees the whole history:

```csharp
var time = provider.GetRequiredService<TimeProvider>();
AgentOutcome? outcome = null;
while (Console.ReadLine() is { Length: > 0 } input)
{
    var state = outcome is null
        ? AgentState.NewTask(input, budget, time.GetUtcNow())          // first turn seeds the conversation
        : outcome.FinalState.WithUserMessage(input, time.GetUtcNow()); // subsequent turns continue it
    outcome = await agent.RunAsync(state);
    Console.WriteLine(outcome.FinalAnswer);
}
```

See `samples/Conversation` (bare REPL) and `samples/ChatSubAgent` (chat agent delegating to a
sub-agent specialist).

## Multi-agent setup

For multi-agent systems use `AddAgentFactory`. Each named agent gets its own isolated
sub-container — `AddStandardAgent` / `AddAgent` mirror the single-agent entry points and
share the same defaults source of truth. Use `AddSubAgentAsTool` to expose one agent as
a tool on another's builder:

```csharp
services.AddAgentFactory(factory =>
{
    factory.AddStandardAgent("research", builder => builder
        .WithSystemPrompt("You are a research specialist.")
        .WithModel(...));

    factory.AddStandardAgent("orchestrator", builder => builder
        .WithSystemPrompt("You are an orchestrator.")
        .WithModel(...)
        .AddSubAgentAsTool("research", factory));
});

await using var provider = services.BuildServiceProvider();
var outcome = await provider.GetRequiredService<AgentFactory>()
    .GetAgent("orchestrator")
    .RunAsync("Research quantum computing and write a brief summary.");
```

Each agent's sensors, model, and budget are fully isolated — nothing leaks between
sub-containers. See `samples/SubAgent` for a runnable no-API-key demo.

`AddSubAgentAsTool` takes an optional `Budget` to bound each delegated run — for example
`AddSubAgentAsTool("research", factory, new Budget { MaxTurns = 4, MaxCost = 0.10m, ... })`. Omit it
and the sub-agent uses the agent's default budget. The sub-agent's cost is returned through the tool
result and counted against the calling agent's budget, so delegation spend is accounted for; the
explicit budget is what bounds a single delegation from overrunning. `samples/ChatSubAgent` passes a
tight budget to its currency specialist.

## Add a tool

```csharp
public sealed class MyTool : ITool
{
    public string Name => "my-tool";
    public string Description => "Does something useful.";
    public JsonElement InputSchema => JsonDocument.Parse("""{ "type": "object" }""").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, "result"));
}
```

Register it in the builder:

```csharp
builder.WithTool<MyTool>()
```

For tools that call external services (HTTP APIs, databases, A2A sub-agents), add Polly
retry + circuit-breaking via the Resilience package:

```csharp
builder.WithResilientTool<MyApiTool>()
```

## Replace the tool registry

`IToolRegistry` is the port that sits between the loop and your tools. It has two jobs: supply the tool list to the guide pipeline each turn (`List()`), and route model-issued tool calls to the right implementation (`DispatchAsync()`).

The default `InMemoryToolRegistry` is a static collection — tools are registered at startup via `builder.WithTool<T>()` and the list never changes while the agent is running. That is fine for most agents, but there are two situations where you want to swap it:

**Dynamic tool sets** — the available tools depend on runtime state (the current user, a feature flag, a remote capability query). A custom registry can compute `List()` and resolve `Get()` dynamically on each call.

**Remote / MCP tooling** — rather than wrapping individual MCP tools as `ITool` instances at startup (see the next section), a registry-backed approach lets the agent discover tools from a remote server at runtime. This is more appropriate when the tool set is large, changes frequently, or is not fully known when the agent is composed.

Replace via the builder:

```csharp
builder.WithToolRegistry<MyToolRegistry>()
// or via factory:
builder.WithToolRegistry(_ => new MyToolRegistry(config))
```

Implement the interface:

```csharp
public sealed class MyToolRegistry : IToolRegistry
{
    public IReadOnlyList<ITool> List()
        // return whatever tools are available right now

    public ITool? Get(string name)
        // resolve by name — return null if not found

    public async Task<ToolResult> DispatchAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var tool = Get(call.ToolName);
        if (tool is null)
            return new ToolResult(call.CallId, $"Tool '{call.ToolName}' not found.", IsError: true);
        return await tool.ExecuteAsync(call, ctx, ct);
    }
}
```

`DispatchAsync` should return an error `ToolResult` for unknown tool names rather than throwing — the loop treats an error result as a clean signal the model can replan from.

## Expose MCP tools

This approach enumerates the MCP server's tools at startup and registers them statically. It is the right choice when the tool set is known upfront and does not change while the agent is running. If your MCP server's tools are dynamic or not fully known at composition time, replace the tool registry instead — see [Replace the tool registry](#replace-the-tool-registry) above.

Add a reference to the [ModelContextProtocol NuGet package](https://www.nuget.org/packages/ModelContextProtocol), create an `McpClient` for your server, then wrap each `McpClientTool` in a thin `ITool` adapter:

```csharp
public sealed class McpTool(McpClient client, McpClientTool mcpTool) : ITool
{
    public string Name => mcpTool.Name;
    public string Description => mcpTool.Description ?? string.Empty;
    public JsonElement InputSchema => mcpTool.JsonSchema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext ctx, CancellationToken ct)
    {
        var args = call.Arguments.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value);
        var result = await client.CallToolAsync(mcpTool.Name, args, cancellationToken: ct);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return new ToolResult(call.CallId, string.IsNullOrEmpty(text) ? "(no text content)" : text, IsError: result.IsError == true);
    }
}
```

Enumerate the server's tools and register them:

```csharp
var mcpTools = await mcpClient.ListToolsAsync();
foreach (var t in mcpTools)
    builder.WithTool(_ => new McpTool(mcpClient, t));
```

## Add a sensor

```csharp
builder.WithSensor<MySensor>()
```

`DefaultSensorRunner` picks it up automatically and runs it in parallel with other sensors
at the same hookpoint. See the [sensor pattern](../README.md#the-sensor-pattern--observing-and-intervening) for how to implement `ISensor`.

## Taint tracking *(Experimental)*

Taint tracking guards against prompt injection attacks that attempt to use external content to trigger privileged side-effecting actions. See the [Prompt injection and taint tracking](../README.md#prompt-injection-and-taint-tracking-experimental) section in the README for the theory and threat model.

**Wiring:**

```csharp
builder.WithTaintTracking(
    untrustedSources: ["fetch_webpage", "read_document", "query_database"],
    privilegedActions: ["send_email",   "execute_code",  "make_payment"]);
```

`untrustedSources` — tools whose results should be treated as potentially hostile external content. As a rule of thumb: any tool that reads content you do not control belongs here. MCP tools should be listed as untrusted sources unless you have explicitly verified and trust the server author.

`privilegedActions` — tools that have real-world side effects and must not run while tainted content is in the trajectory. If a tool can send data somewhere, modify state outside the agent, or execute arbitrary logic, it belongs here.

> ⚠️ **This sensor fails closed.**
>
> Once a result from an untrusted source enters the trajectory, **all** privileged actions are blocked for the remainder of that agent run — regardless of how many turns have passed or how unrelated the subsequent reasoning appears to be. The sensor has no way to determine whether the model's reasoning has been influenced by the tainted content, so it assumes the worst.
>
> **You must account for this in your agent design.** Concretely:
>
> - If your agent legitimately needs to fetch external content *and* take a privileged action in the same run, the blocked action will surface as a tool error. The model will replan — but it cannot complete the task without human intervention.
> - The intended escape hatch is `ask_human`: if `WithAskHumanTool` is configured, instruct the model in the system prompt to call it when a privileged action is blocked, so a human can review and authorise the action in context.
> - If no human channel is available, the model will return whatever partial answer it can — callers should be prepared to receive an incomplete result.

**System prompt guidance:**

Because the sensor blocks at the tool level but cannot force the model to escalate correctly, your system prompt should tell the model what to do when a block fires. For example:

```
If a privileged action is blocked by the harness, do not retry it.
If a human operator is available, call ask_human with full context
so they can review and authorise the action. Otherwise, return what
you have found so far and explain why you could not complete the task.
```

## Add a guide

```csharp
builder.WithGuide<MyGuide>() // runs after the eight built-in guides
```

See the [guide pattern](../README.md#the-guide-pattern--shaping-perception) for how to implement `IGuide`.

## Replace the trajectory guide

The default `HeadEvictionTrajectoryGuide` evicts the oldest steps when the trajectory exceeds the context budget, replacing them with a placeholder via `ICompactionStrategy`. Swap it for a different eviction or rendering strategy via `builder.WithTrajectoryGuide<T>()`:

```csharp
public sealed class MyTrajectoryGuide(ICompactionStrategy compactionStrategy) : ITrajectoryGuide
{
    public string Name => "my-trajectory";

    public async Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        // Write rendered steps into draft.TrajectoryMessages however you like.
        // draft.TrajectoryMessages is empty when this is called — write everything here.
        // Use state.Budget.MaxContextTokens and state.Trajectory for inputs.
    }
}
```

Register it:

```csharp
builder.WithTrajectoryGuide<MyTrajectoryGuide>()
```

`ITrajectoryGuide` is kept separate from `IGuide` so `DefaultGuideRunner` can guarantee it always runs last — after all supporting guides have written to `ContextDraft` — without relying on DI registration order. Any implementation can structure `ContributeAsync` however it needs to; there is no shared base class forcing a particular eviction or compaction shape. `ICompactionStrategy` is available as a constructor dependency if the implementation evicts steps and wants to delegate the replacement text, but it is not required.

## Tracers

Tracers are additive — call `WithTracer` (or its convenience variants) multiple times and
they are automatically composed into a `CompositeTracer` at resolution time:

```csharp
builder
    .WithConsoleTracer()   // human-readable stdout — handy for local dev
    .WithOtelTracer()      // OpenTelemetry spans + metrics — handy for production
```

Implement `ITracer` and register via `WithTracer<T>()` or `WithTracer(factory)` for a
custom backend.

## Checkpoint / resume and human-in-the-loop

These two features share the same underlying mechanism — `ICheckpointStore` — but serve
different purposes. There are three distinct use cases:

| Use case | API | When to use |
|---|---|---|
| Crash recovery only | `WithFileCheckpointStore(dir)` | Long-running tasks with no human involvement |
| HITL without durability | `WithAskHumanTool<TNotifier>()` | In-process demos, or when the caller manages state externally |
| HITL with durability *(most common)* | `WithHITL<TNotifier>(store)` | Production async human-in-the-loop |

### Crash recovery only

```csharp
builder.WithFileCheckpointStore("/var/checkpoints")
```

The loop saves a checkpoint at the start of each turn. To resume after a crash:

```csharp
var store = provider.GetRequiredService<ICheckpointStore>();
var latest = await store.LoadLatestAsync(taskId, ct);

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync(latest!.State with { Status = AgentStatus.Running });
```

Implement `ICheckpointStore` to target blob storage, a database, or any other backend.

### HITL without durability

Use `WithAskHumanTool` when the caller manages state externally (e.g. in an orchestrator
with its own persistence layer), or for in-process development demos where the original
process stays alive for the duration of the human wait:

```csharp
// Development — ConsoleHumanChannel prints the question; your loop reads stdin after RunAsync returns
builder.WithAskHumanTool<ConsoleHumanChannel>()

// Production — implement IHumanNotifier for your delivery mechanism, and manage state yourself
builder.WithAskHumanTool(_ => new SlackHumanNotifier(slackClient, channelId))
```

Without a checkpoint store, if the process dies while waiting for the human the run state
is lost. Use `WithHITL` below if that is not acceptable.

### HITL with durability *(most common)*

HITL is inherently cross-process — the human answers minutes or days later, long after the
original process may have exited. `WithHITL` registers the `ask_human` tool and a checkpoint
store together, making the coupling explicit:

```csharp
// Most common: file-backed checkpoint store
builder.WithHITL<SlackHumanNotifier>(new FileCheckpointStore("/var/checkpoints"))

// Generic form — when TStore is DI-resolvable with no constructor arguments
builder.WithHITL<SlackHumanNotifier, MyBlobCheckpointStore>()
```

When the model calls `ask_human`, the loop:
1. Builds a suspended state with `Status = AwaitingHuman` and `PendingHumanInput` set
2. Saves that state to the checkpoint store
3. Fires `IHumanNotifier.NotifyAsync` (fire-and-forget)
4. Returns `AgentOutcome { Status = AwaitingHuman }`

The checkpoint is self-describing — `State.PendingHumanInput` carries the call ID and
question, so a process that restarts can load the checkpoint and know exactly what to do
without needing the original `AgentOutcome` in memory.

```mermaid
sequenceDiagram
    participant Caller
    participant HarnessLoop
    participant Store as ICheckpointStore
    participant Notifier as IHumanNotifier

    Caller->>HarnessLoop: RunAsync(state)
    HarnessLoop->>Store: SaveAsync(suspended state)
    HarnessLoop->>Notifier: NotifyAsync(question)
    note right of Notifier: HTTP / Slack / queue...
    Notifier-->>HarnessLoop: (returns immediately)
    HarnessLoop-->>Caller: AgentOutcome { Status = AwaitingHuman }

    note over Caller: human answers — seconds or days later
    note over Caller: process may have restarted

    Caller->>Store: LoadLatestAsync(taskId)
    Store-->>Caller: Checkpoint { State.Status = AwaitingHuman,<br/>State.PendingHumanInput = { CallId, Question } }
    Caller->>Caller: state = checkpoint.State<br/>.ResumeWithHumanAnswer(callId, answer)
    Caller->>HarnessLoop: RunAsync(resumedState)
    HarnessLoop-->>Caller: AgentOutcome { Status = Done }
```

**The suspend/resume cycle:**

```csharp
var outcome = await agent.RunAsync(task, budget);

while (outcome.Status == AgentStatus.AwaitingHuman)
{
    // FinalState.PendingHumanInput is the durable form — present whether you loaded
    // from a checkpoint or are continuing in-process after RunAsync returned.
    var pending = outcome.FinalState.PendingHumanInput!;
    var answer = await GetHumanAnswerAsync(pending.CallId);

    outcome = await agent.RunAsync(
        outcome.FinalState.ResumeWithHumanAnswer(pending.CallId, answer));
}
```

**Resuming after a crash:**

```csharp
var store = provider.GetRequiredService<ICheckpointStore>();
var checkpoint = await store.LoadLatestAsync(taskId, ct);

if (checkpoint?.State.Status == AgentStatus.AwaitingHuman)
{
    // Process restarted mid-wait — obtain the answer then resume
    var pending = checkpoint.State.PendingHumanInput!;
    var answer = await GetHumanAnswerAsync(pending.CallId);
    var state = checkpoint.State.ResumeWithHumanAnswer(pending.CallId, answer);
    outcome = await agent.RunAsync(state);
}
else if (checkpoint is not null)
{
    // Crash during a non-HITL turn — replay the interrupted turn
    outcome = await agent.RunAsync(checkpoint.State with { Status = AgentStatus.Running });
}
```

`ResumeWithHumanAnswer` replaces the pending `ToolCallStep` in the trajectory with the real
answer and clears `PendingHumanInput` — the model sees it as a normal completed tool call
on its next turn and continues from there.

See `samples/HitlSuspendResume` for a runnable in-process demo and `samples/CheckpointResume`
for crash-recovery checkpointing.

## Budget enforcement

```csharp
builder.WithBudgetEnforcer<MyBudgetEnforcer>()
```

Implement `IBudgetEnforcer` to replace the default policy with dynamic limits — per-user
quotas, cost allocation, or anything driven by runtime state. See the
[Budget enforcement](../README.md#budget-enforcement) section for how exhaustion works.

## Rate limiting

Provider APIs enforce sliding-window limits (calls per minute, tokens per minute) that vary
by account tier. The harness checks `IRateLimiter` before each model call and waits if the
limit is hit. By default no rate limiting is applied (`NullRateLimiter`).

Both built-in implementations inspect the trajectory's `ModelCallStep` timestamps — no
mutable state is needed in the limiter itself.

```csharp
// Calls-per-minute cap — counts model calls in the last 60 s
builder.WithRateLimiter(_ => new CallsPerMinuteRateLimiter(callsPerMinute: 50))

// Tokens-per-minute cap — sums input + output tokens in the last 60 s
builder.WithRateLimiter(_ => new TokensPerMinuteRateLimiter(tokensPerMinute: 100_000))

// Both together — the harness automatically composes them and respects the most restrictive limit
builder
    .WithRateLimiter(_ => new CallsPerMinuteRateLimiter(callsPerMinute: 50))
    .WithRateLimiter(_ => new TokensPerMinuteRateLimiter(tokensPerMinute: 100_000))
```

When a limit is hit the harness waits for the retry window, then continues. If the wait
would exceed `MaxWallClock` it triggers the budget-exhaustion path instead (one final
model call, `PartialResult`). Implement `IRateLimiter` and register via `WithRateLimiter`
for custom strategies (per-user quotas, burst allowances, etc.).

## Skills

Give an agent pre-authored `SKILL.md` instructions it can read but not modify — useful
for domain knowledge, standard operating procedures, or any fixed guidance you want
available across runs. Uses the [agentskills.io](https://agentskills.io) format.

```csharp
builder.WithSkills("~/.skills/builtin")
```

`SkillsGuide` surfaces the catalogue automatically; `skill_view` lets the model load
the full body on demand. No separate tool wiring needed.

## Learning

Enable the agent to accumulate its own skills over time. See the
[Agent Learning](../README.md#agent-learning-experimental) section for the full explanation.

```csharp
builder.WithLearning("~/.skills/learned")
```

Chain both together to give the agent pre-authored skills *and* the ability to learn:

```csharp
builder
    .WithSkills("~/.skills/builtin")
    .WithLearning("~/.skills/learned")
```

## Swap the model client

```csharp
// Anthropic (via Infrastructure.Anthropic)
builder.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
{
    ApiKey = apiKey,
    ModelId = "claude-haiku-4-5-20251001"
}))

// Ollama — local inference (via Infrastructure.Ollama)
builder.WithOllamaModel(new OllamaClientOptions { ModelId = "qwen2.5:7b" })

// Azure AI Foundry / Azure OpenAI Service (via Infrastructure.AzureOpenAI)
// ApiKey = null uses DefaultAzureCredential (managed identity) — recommended for production
builder.WithResilientModel(_ => new AzureOpenAIModelClient(new AzureOpenAIClientOptions
{
    Endpoint = new Uri("https://your-resource.openai.azure.com"),
    DeploymentName = "gpt-4o",
    ApiKey = null
}))
```

For resilience (Polly retry + circuit-breaker), use `WithResilientModel` from the
Resilience package. Pass a custom `ResiliencePipeline<ModelResponse>` as a second
argument to override the default policy.

## Enable AI-powered compaction

By default, when the trajectory is trimmed to fit the context window, `HeadEvictionTrajectoryGuide` inserts a bare omission note (`[N earlier step(s) omitted — context window limit]`). On long runs this loses signal the model may still need. `AiCompactionStrategy` replaces the note with a prose summary generated by a lightweight model:

```csharp
builder.WithAiCompaction(new ClaudeModelClient(new ClaudeClientOptions
{
    ApiKey = apiKey,
    ModelId = "claude-haiku-4-5-20251001"   // dedicated compaction model
}))
```

The strategy fails open — if the model call fails or returns empty text, the bare omission note is used instead, so a compaction failure never blocks the run. Use a fast, cheap model (Haiku-class); compaction calls are infrequent on well-scoped runs but could stack up on very long ones.
