using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Ollama;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var services = new ServiceCollection();

services.AddStandardModelHarness(builder => builder
    .WithSystemPrompt("You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.")
    .WithConsoleTracer()
    .WithTool<EchoTool>()
    .WithTool<CalculatorTool>()
    .WithOllamaModel(new OllamaClientOptions
    {
        BaseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434",
        ModelId = config["Ollama:ModelId"] ?? "llama3.2"
    }));

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync("What is 56 multiplied by 13?");

AgentConsoleWriter.PrintHeader("ollama-tool-call", "Runs a tool-calling task through a local Ollama model — demonstrates ToolUse/Tool message grouping.");
AgentConsoleWriter.PrintOutcome(outcome);
