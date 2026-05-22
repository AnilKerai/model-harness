using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.Samples.Common;
using SapphireGuard.ModelHarness.Samples.ToolCallReasonableness;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using FakeModelClient.");

var services = new ServiceCollection();

services.AddModelHarness(
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.");

services
    .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddSingleton<ITool, EchoTool>()
    .AddSingleton<ITool, CalculatorTool>()
    .AddSingleton<ISensor, ToolCallReasonablenessSensor>()
    .AddSingleton<ISensor, StuckDetector>();

if (usingRealModel)
    services.AddModelClient(_ => new ResilientModelClientDecorator(
        new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = apiKey!,
            ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5"
        })));
else
    services.AddModelClient(_ => new FakeModelClient());

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync("What is 100 divided by 0?");

AgentConsoleWriter.PrintHeader("tool-call-reasonableness", "ToolCallReasonablenessSensor should block the division-by-zero call before it dispatches.");
AgentConsoleWriter.PrintOutcome(outcome);
