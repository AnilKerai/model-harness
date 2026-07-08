using GettingStarted;
using GettingStarted.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var anthropicKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(anthropicKey);
if (!usingRealModel)
    Console.WriteLine("No Anthropic:ApiKey set — running the scripted stand-in model (no key needed).\n");

var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        .WithSystemPrompt(
            """
            You are a friendly customer-support assistant for an online shop.
            When a customer asks about an order, load the answer-order-enquiry skill with skill_view and follow it.
            """)
        .WithSkills(skillsDir)                 // procedural memory: a reusable procedure the agent loads on demand
        .WithTool<OrderStatusTool>()           // the one action the agent can take
        .WithSensor<PiiRedactionSensor>()      // safety net: block any reply that leaks the customer's email
        .WithConsoleTracer();                  // stream the loop's decisions to stdout

    if (usingRealModel)
        builder.WithClaudeModel(new ClaudeClientOptions
        {
            ApiKey = anthropicKey!,
            ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5"
        });
    else
        builder.WithModel(_ => new SupportScriptedModelClient());
});

await using var provider = services.BuildServiceProvider();

var task = args.Length > 0
    ? string.Join(' ', args)
    : "A customer emailed asking: \"Where is my order A1001?\" Look it up and write them a friendly, personal reply.";

Console.WriteLine($"Task: {task}\n");

var outcome = await provider.GetRequiredService<Agent>().RunAsync(
    task,
    new Budget { MaxTurns = 6, MaxTotalTokens = 60_000, MaxCost = 0.25m, MaxWallClock = TimeSpan.FromMinutes(2) });

Console.WriteLine();
Console.WriteLine(outcome.Status == AgentStatus.Done
    ? outcome.FinalAnswer
    : $"Agent did not complete. Status: {outcome.Status}. {outcome.FailureReason}");
