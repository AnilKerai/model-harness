# Running the samples

Each scenario is its own console project under `samples/`. The samples wire up
`ClaudeModelClient` against the Anthropic API, `AzureOpenAIModelClient` against
Azure AI Foundry / Azure OpenAI Service, and `OllamaModelClient` against a local
Ollama instance. A `FakeModelClient` is also provided for local development with
no external dependencies.

## Anthropic

Add your API key to `samples/HappyPath/appsettings.local.json`:

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  }
}
```

```bash
dotnet run --project samples/HappyPath
```

## Azure AI Foundry / Azure OpenAI Service

Add your endpoint and deployment name to `samples/AzureOpenAI/appsettings.local.json`.
Omit `ApiKey` to use `DefaultAzureCredential` (managed identity):

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com",
    "DeploymentName": "gpt-4o",
    "ApiKey": "optional — omit to use DefaultAzureCredential"
  }
}
```

```bash
dotnet run --project samples/AzureOpenAI
```

## Ollama (local inference)

Pull the model first, then add it to `samples/OllamaToolCall/appsettings.local.json`:

```bash
ollama pull qwen2.5:7b
```

```json
{
  "Ollama": { "ModelId": "qwen2.5:7b" }
}
```

```bash
dotnet run --project samples/OllamaToolCall
```

## No API key

Several samples use `FakeModelClient` and need no external dependencies:

```bash
dotnet run --project samples/SubAgent
dotnet run --project samples/SkillLearning
```

## What you'll see

Two things stream to stdout: JSON trace events as they happen, then a formatted summary once the run completes.

Here's the full output of `samples/HappyPath` on the task *"What is 124 multiplied by 37?"*:

```
{"evt":"trace_started","taskId":"7162a0ca...","taskText":"What is 124 multiplied by 37?","ts":"..."}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"progress-check","intervene":false}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"prompt-injection","intervene":false}
{"evt":"model_call",...,"stopReason":"ToolUse","toolCalls":1,"usage":{"input":745,"output":88},"cost":0.000948}
{"evt":"sensor_result",...,"hookPoint":"PreToolCall","sensor":"stuck-detector","intervene":false}
{"evt":"tool_call",...,"tool":"calculator","args":"{\"op\":\"mul\",\"lhs\":124,\"rhs\":37}","resultPreview":"4588"}
{"evt":"sensor_result",...,"hookPoint":"PostToolCall","sensor":"prompt-injection","intervene":false}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"progress-check","intervene":false}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"prompt-injection","intervene":false}
{"evt":"model_call",...,"stopReason":"EndTurn","toolCalls":0,"textPreview":"124 multiplied by 37 is **4,588**.","cost":0.0008}
{"evt":"trace_completed",...,"status":"Done"}

────────────────────────────────────────────────────────────
  happy-path
  Normal arithmetic — all sensors should pass.
────────────────────────────────────────────────────────────

Status      : Done
FinalAnswer : 124 multiplied by 37 is **4,588**.

Trajectory:
  [model]   stop=ToolUse    tools=1 cost=$0.0009
  [tool]    calculator({"op":"mul","lhs":124,"rhs":37}) → 4588
  [model]   stop=EndTurn    tools=0 cost=$0.0008
```

**What's happening:**

- **`trace_started`** — the loop begins; each run gets a unique `taskId` for correlating events.
- **`sensor_result` (PreModelCall)** — sensors run in parallel before every model call. All pass here (`intervene: false`), so the loop proceeds normally.
- **`model_call` (stop=ToolUse)** — turn 1: the model decides to call the `calculator` tool rather than answer directly. Token usage and cost are tracked per call.
- **`sensor_result` (PreToolCall)** — sensors check the tool call before it's dispatched. The `stuck-detector` confirms the model isn't looping.
- **`tool_call`** — the calculator runs with `mul(124, 37)` and returns `4588`. Duration is sub-millisecond for an in-process tool.
- **`sensor_result` (PostToolCall)** — sensors inspect the result after dispatch. Advisory only at this hookpoint.
- **`model_call` (stop=EndTurn)** — turn 2: the model has the tool result and produces a final answer. No tool calls this time, so the loop checks `PreReturn` sensors and returns.
- **`trace_completed`** — the run is done with status `Done`.

The **trajectory summary** at the end is the flattened log of every step — the same data as the JSON events but in a compact, human-readable form. It's the fastest way to see what the agent actually did.

## Seeing a sensor intervene

`samples/PiiDetection` shows what happens when a sensor fires. The task instructs the model to address the user by their email address in the response — the `PiiRedactionSensor` catches this at `PostModelCall` and forces a clean retry.

**Task:**
```
The user's email address is john.smith@acmecorp.com. Calculate 124 multiplied by 37,
then address the user by their email address when presenting the result.
```

```
{"evt":"model_call",...,"stopReason":"EndTurn","textPreview":"I've already calculated 124 multiplied by 37...","cost":0.0013}
{"evt":"sensor_result",...,"hookPoint":"PostModelCall","sensor":"pii-redaction","intervene":true,
  "reason":"Response contains possible PII (email: \"john.smith@acmecorp.com\"). Restate your answer without including any personal data."}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"progress-check","intervene":false}
{"evt":"sensor_result",...,"hookPoint":"PreModelCall","sensor":"prompt-injection","intervene":false}
{"evt":"model_call",...,"stopReason":"EndTurn","textPreview":"I appreciate you testing my safety guidelines...","cost":0.0013}
{"evt":"sensor_result",...,"hookPoint":"PostModelCall","sensor":"pii-redaction","intervene":false}
{"evt":"trace_completed",...,"status":"Done"}

Trajectory:
  [model]   stop=ToolUse    tools=1 cost=$0.0010
  [tool]    calculator({"op":"mul","lhs":124,"rhs":37}) → 4588
  [model]   stop=EndTurn    tools=0 cost=$0.0013
  [HARNESS OBSERVATION — pii-redaction @ PostModelCall] Response contains possible PII (email: "john.smith@acmecorp.com"). Restate your answer without including any personal data.
  [model]   stop=EndTurn    tools=0 cost=$0.0013
```

**What's happening:**

- **`sensor_result` (PostModelCall, intervene=true)** — the PII sensor detected an email address in the model's response. Because this is a `PostModelCall` intervention, the harness **rejects** the response — it is suppressed from the trajectory so the model cannot re-see it.
- **Fresh turn** — the model gets another call. The harness observation (`[HARNESS OBSERVATION — ...]`) is injected into the context so the model understands why it's being asked again.
- **Second `model_call`** — the model produces a clean response with no PII. The sensor passes this time (`intervene: false`) and the run completes.

Notice the trajectory: the rejected response never appears as a `[model]` step — only the harness observation and the clean retry are recorded. The model's first (flagged) response is gone as far as the trajectory is concerned. This is the `PostModelCall` hookpoint's **reject** behaviour in action.

The final answer the caller receives:

```
124 × 37 = **4,588**

Even though you've asked me to address you by email, I should not include personal
information like email addresses in my responses. If you need to reach the user
directly, I'd recommend sending them a separate message.
```

The model answered the task correctly and declined to echo the PII — without any explicit instruction in the system prompt about email addresses. The sensor caught it and the model self-corrected.
