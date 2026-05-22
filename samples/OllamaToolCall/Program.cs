using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Ollama;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var services = new ServiceCollection();

services.AddModelHarness(
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.");

services
    .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddSingleton<ITool, EchoTool>()
    .AddSingleton<ITool, CalculatorTool>()
    .AddSingleton<ISensor, StuckDetector>()
    .AddOllamaModelClient(new OllamaClientOptions
    {
        BaseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434",
        ModelId = config["Ollama:ModelId"] ?? "llama3.2"
    });

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<HarnessLoop>()
    .RunAsync(AgentState.NewTask(
        taskText: "What is 56 multiplied by 13?",
        budget: new Budget { MaxTurns = 8, MaxContextTokens = 100_000, MaxCostUsd = 0m, MaxWallClock = TimeSpan.FromSeconds(60) }),
        CancellationToken.None);

AgentConsoleWriter.PrintHeader("ollama-tool-call", "Runs a tool-calling task through a local Ollama model — demonstrates ToolUse/Tool message grouping.");
AgentConsoleWriter.PrintOutcome(outcome);
