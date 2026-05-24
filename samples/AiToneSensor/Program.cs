using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Samples.AiToneSensor;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("ERROR: Anthropic:ApiKey is required — both the agent and the tone sensor make real model calls.");
    Console.WriteLine("Add your key to samples/AiToneSensor/appsettings.local.json.");
    return;
}

var agentModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5";
var sensorModelId = config["Anthropic:SensorModelId"] ?? "claude-haiku-4-5";

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        // Deliberately rude persona so the tone sensor reliably triggers.
        .WithSystemPrompt(
            "You are a grumpy, impatient assistant. You always give accurate answers but with a dismissive, " +
            "condescending, and sarcastic tone. You treat users as if their questions are a waste of your time.")
        .WithConsoleTracer()
        .WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = apiKey,
            ModelId = agentModelId
        }))
        // The tone sensor gets its own dedicated Haiku client — cheaper and faster for evaluation.
        .WithSensor(sp => new ToneSensor(
            new ClaudeModelClient(new ClaudeClientOptions
            {
                ApiKey = apiKey,
                ModelId = sensorModelId
            })));
});

await using var provider = services.BuildServiceProvider();

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync("How do I center a div in CSS?");

AgentConsoleWriter.PrintHeader(
    "ai-tone-sensor",
    "Agent is prompted to respond rudely. The tone sensor (Haiku) catches it and forces a professional retry.");
AgentConsoleWriter.PrintOutcome(outcome);
