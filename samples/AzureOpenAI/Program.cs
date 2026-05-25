using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI;
using SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var endpoint = config["AzureOpenAI:Endpoint"];
var deploymentName = config["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
var apiKey = config["AzureOpenAI:ApiKey"];

var usingRealModel = !string.IsNullOrWhiteSpace(endpoint);

if (!usingRealModel)
    Console.WriteLine("WARNING: AzureOpenAI:Endpoint not configured — using FakeModelClient.");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        .WithSystemPrompt("You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.")
        .WithConsoleTracer()
        .WithTool<EchoTool>()
        .WithTool<CalculatorTool>();

    if (usingRealModel)
        builder.WithResilientModel(_ => new AzureOpenAIModelClient(new AzureOpenAIClientOptions
        {
            Endpoint = new Uri(endpoint!),
            DeploymentName = deploymentName,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey
        }));
    else
        builder.WithModel(_ => new FakeModelClient());
});

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync("What is 124 multiplied by 37?");

AgentConsoleWriter.PrintHeader(
    "azure-openai",
    $"Arithmetic via Azure AI Foundry ({deploymentName}). Configure AzureOpenAI:Endpoint in appsettings.local.json; " +
    "omit AzureOpenAI:ApiKey to use DefaultAzureCredential (managed identity).");
AgentConsoleWriter.PrintOutcome(outcome);
