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

## Output

JSON trace events stream to stdout, followed by the final outcome and a flattened trajectory.
