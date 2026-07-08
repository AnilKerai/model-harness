# Getting started

The smallest end-to-end **model-harness** agent, wired from the published NuGet packages. It's a tiny customer-support assistant that shows the three things which make model-harness a *harness*, not just a tool-calling SDK:

- **a tool** — `get_order_status` (canned data, no database)
- **a sensor** — the built-in `PiiRedactionSensor` blocks a reply that leaks the customer's email and makes the agent try again
- **a skill** — `answer-order-enquiry`, a short procedure the agent loads on demand with `skill_view`

## Run it — no API key needed

```bash
dotnet run
```

With no key configured the sample uses a scripted stand-in model ([`SupportScriptedModelClient`](SupportScriptedModelClient.cs)), so you can watch the whole flow — load the skill, look up the order, get the PII-leaking reply blocked, then a clean retry — without spending anything. (Or open `GettingStarted.slnx` in your IDE.)

## Swap the fake model for a real one

Drop an `appsettings.local.json` next to `appsettings.json` with an Anthropic API key:

```json
{
  "Anthropic": {
    "ApiKey": "sk-ant-..."
  }
}
```

Run again — [`Program.cs`](Program.cs) sees the key and swaps the scripted model for `ClaudeModelClient` automatically, no code change. The file is gitignored, so your key stays local. Set `Anthropic:ModelId` to choose a model (defaults to `claude-haiku-4-5`).

Want a different provider — Azure OpenAI, Ollama, or your own adapter? That's one line: see **[Swap the model client](../docs/EXTENDING.md#swap-the-model-client)**.

## Where next

- **[Main README](../README.md)** — what model-harness is, the extension points, and the two core patterns
- **[EXTENDING.md](../docs/EXTENDING.md)** — recipes for tools, sensors, guides, skills, HITL, checkpoint/resume, and more
- **[RUNNING.md](../docs/RUNNING.md)** — the fuller set of focused samples under `samples/`
