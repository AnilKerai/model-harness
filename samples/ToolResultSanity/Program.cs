using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;

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
services.AddModelHarness(builder =>
{
    builder
        .WithSystemPrompt("You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.")
        .WithConsoleTracer()
        .WithOtelTracer()
        .WithToolRegistry<InMemoryToolRegistry>()
        .WithTool<EchoTool>()
        .WithTool<CalculatorTool>()
        .WithSensor<StuckDetector>()
        .WithSensor(_ => new ToolResultSanityCheckSensor(
            toolValidators: new Dictionary<string, Func<string, string?>>
            {
                ["calculator"] = result =>
                    double.TryParse(result, out var value) && value > 1000
                        ? $"result {value} exceeds the maximum allowed value of 1000"
                        : null
            }));
    if (usingRealModel)
        builder.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = apiKey!,
            ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5"
        }));
    else
        builder.WithModel(_ => new FakeModelClient());
});
await using var provider = services.BuildServiceProvider();
var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync("What is 124 multiplied by 37?");
AgentConsoleWriter.PrintHeader("tool-result-sanity", "124 × 37 = 4588, which exceeds the business-rule cap of 1000 — ToolResultSanityCheckSensor should flag it.");
AgentConsoleWriter.PrintOutcome(outcome);
