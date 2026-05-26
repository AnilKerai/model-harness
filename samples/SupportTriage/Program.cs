using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;
using SapphireGuard.ModelHarness.Samples.SupportTriage;
using SapphireGuard.ModelHarness.Samples.SupportTriage.Tools;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var provider = config["Provider"] ?? "fake";
var anthropicKey = config["Anthropic:ApiKey"];
var anthropicModel = config["Anthropic:ModelId"] ?? "claude-haiku-4-5";
var azureEndpoint = config["AzureOpenAI:Endpoint"];
var azureDeployment = config["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
var azureKey = config["AzureOpenAI:ApiKey"];

var usingRealModel = provider switch
{
    "anthropic" => !string.IsNullOrWhiteSpace(anthropicKey),
    "azure"     => !string.IsNullOrWhiteSpace(azureEndpoint),
    _           => false
};

if (!usingRealModel)
    Console.WriteLine("WARNING: No real model credentials configured — using FakeModelClient.");

AgentConsoleWriter.PrintHeader(
    "support-triage",
    "Multi-ticket support triage: tool use, PII redaction, prompt injection detection, and HITL escalation.");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        .WithSystemPrompt(
            """
            You are a support triage agent. For each ticket you receive:
            1. Look up the customer with lookup_customer.
            2. Retrieve their ticket history with get_ticket_history.
            3. Search for relevant known issues with search_known_issues.
            4. If you can resolve it, call draft_response with a clear, friendly reply.
            5. If the issue is complex, high-severity, or requires human judgement (e.g. Enterprise P1, billing disputes you cannot resolve, security concerns), call ask_human with full context so a human operator can decide.
            Never include customer phone numbers, full email addresses, or other personal data in your responses.
            """)
        .WithConsoleTracer()
        .WithTool<LookupCustomerTool>()
        .WithTool<GetTicketHistoryTool>()
        .WithTool<SearchKnownIssuesTool>()
        .WithTool<DraftResponseTool>()
        .WithAskHumanTool<ConsoleHumanChannel>()
        .WithSensor<PiiRedactionSensor>();

    if (usingRealModel && provider == "anthropic")
        builder.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = anthropicKey!,
            ModelId = anthropicModel
        }));
    else if (usingRealModel && provider == "azure")
        builder.WithResilientModel(_ => new AzureOpenAIModelClient(new AzureOpenAIClientOptions
        {
            Endpoint = new Uri(azureEndpoint!),
            DeploymentName = azureDeployment,
            ApiKey = string.IsNullOrWhiteSpace(azureKey) ? null : azureKey
        }));
    else
        builder.WithModel(_ => new FakeModelClient());
});

await using var serviceProvider = services.BuildServiceProvider();
var agent = serviceProvider.GetRequiredService<Agent>();

var budget = new Budget
{
    MaxTurns = 10,
    MaxContextTokens = 100_000,
    MaxCostUsd = 0.10m,
    MaxWallClock = TimeSpan.FromMinutes(1)
};

foreach (var ticket in TicketDatabase.Tickets)
{
    Console.WriteLine();
    Console.WriteLine($"══ Processing {ticket.Id} ═══════════════════════════════════════════");
    Console.WriteLine($"   From   : {ticket.CustomerEmail}");
    Console.WriteLine($"   Subject: {ticket.Subject}");
    Console.WriteLine($"   Body   : {ticket.Body}");
    Console.WriteLine();

    var prompt =
        $"Ticket ID : {ticket.Id}\n" +
        $"From      : {ticket.CustomerEmail}\n" +
        $"Subject   : {ticket.Subject}\n" +
        $"Body      : {ticket.Body}";

    var outcome = await agent.RunAsync(prompt, budget);

    if (outcome.Status == AgentStatus.AwaitingHuman && outcome.PendingHumanInput is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"── Run suspended — awaiting human input for {ticket.Id} ──────────────");
        Console.WriteLine($"   Question: {outcome.PendingHumanInput.Question}");
        Console.Write("   Your answer: ");
        var answer = Console.ReadLine() ?? string.Empty;

        var resumed = outcome.FinalState.ResumeWithHumanAnswer(outcome.PendingHumanInput.CallId, answer);
        outcome = await agent.RunAsync(resumed);
    }

    AgentConsoleWriter.PrintOutcome(outcome);
}
