using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Output;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Samples.Common;
using SapphireGuard.ModelHarness.Samples.StructuredOutput;
using SapphireGuard.ModelHarness.Samples.StructuredOutput.Tools;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var anthropicKey = config["Anthropic:ApiKey"];
var anthropicModel = config["Anthropic:ModelId"] ?? "claude-haiku-4-5";
var usingRealModel = !string.IsNullOrWhiteSpace(anthropicKey);

if (!usingRealModel)
    Console.WriteLine("WARNING: No Anthropic API key configured — using ScriptedTriageModel.");

AgentConsoleWriter.PrintHeader(
    "structured-output",
    "A typed final answer: the schema is stated by a guide, enforced by a PreReturn sensor.");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        .WithSystemPrompt(
            """
            You are a support triage agent. Look up the customer with lookup_customer, then triage
            the ticket.
            """)
        .WithConsoleTracer()
        .WithTool<LookupCustomerTool>()
        // Registers the contract, a guide that states its schema in the system prompt every turn, and a
        // PreReturn sensor that binds the final answer against it — challenging the model with the
        // binder's own error, and giving it a fresh turn, rather than throwing.
        .WithStructuredOutput<TriageResult>();

    if (usingRealModel)
        builder.WithModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = anthropicKey!,
            ModelId = anthropicModel
        }));
    else
        builder.WithModel<ScriptedTriageModel>();
});

await using var serviceProvider = services.BuildServiceProvider();
var agent = serviceProvider.GetRequiredService<Agent>();

// The same contract the guide states and the sensor enforces — so the read side cannot drift from
// what was validated.
var contract = serviceProvider.GetRequiredService<StructuredOutputContract<TriageResult>>();

var outcome = await agent.RunAsync(
    """
    Ticket TCK-4471 from ada@contoso.com
    Subject: Charged twice for February
    Body: My February invoice was billed to my card twice. Please refund the duplicate.
    """,
    new Budget
    {
        MaxTurns = 8,
        MaxTotalTokens = 100_000,
        MaxCost = 0.10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    });

AgentConsoleWriter.PrintOutcome(outcome);

Console.WriteLine();
if (contract.TryBind(outcome.FinalAnswer, out var triage, out var error))
{
    Console.WriteLine("Bound to TriageResult:");
    Console.WriteLine($"  Category : {triage!.Category}");
    Console.WriteLine($"  Priority : {triage.Priority}");
    Console.WriteLine($"  Summary  : {triage.Summary}");
}
else
{
    // Reachable when the run ends in PartialResult — the budget or the intervention cap cut it short
    // before the model produced a conforming answer.
    Console.WriteLine($"No structured result: {error}");
}
