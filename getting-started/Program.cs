using GettingStarted;
using GettingStarted.Sensors;
using GettingStarted.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Security;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var anthropicKey = config["Anthropic:ApiKey"];
var braveKey     = config["Brave:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(anthropicKey);

if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using FakeModelClient.");
if (string.IsNullOrWhiteSpace(braveKey))
    Console.WriteLine("WARNING: Brave:ApiKey not configured — web_search will return an error.");

var queryStore = new QueryStore();
var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("ModelHarness/1.0 (debtor-verification)");

var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");

var services = new ServiceCollection();

services.AddStandardModelHarness(builder =>
{
    builder
        .WithSystemPrompt(
            """
            You are a credit control assistant. You help credit controllers verify debtor legitimacy and support funding decisions.

            Before starting any verification task, load the relevant skill with skill_view and follow its procedure exactly.

            If you identify a hard red flag during verification — a failed Companies House registration, a mismatched domain, or an invalid AP email — raise a Jira ticket with create_jira_ticket before completing your report.
            """)
        .WithSkills(skillsDir)
        .WithTool(_ => new SubmitQueryTool(queryStore))
        .WithTool(_ => new FetchQueryResultsTool(queryStore))
        .WithTool(_ => new WebSearchTool(braveKey ?? "", http))
        .WithTool(_ => new WebFetchTool(http))
        .WithTool<CreateJiraTicketTool>()
        .WithConsoleTracer()
        // Infrastructure sensors
        .WithSensor<PromptInjectionSensor>()
        .WithSensor<StuckDetector>()
        .WithSensor(_ => new ToolResultSanityCheckSensor())
        .WithSensor(_ => new TaintTrackingSensor(new TrustPolicy(
            untrustedSources:  ["web_search", "web_fetch"],
            privilegedActions: [])))
        // Domain sensors
        .WithSensor<OutputFormatSensor>()
        .WithSensor<HardRedFlagSensor>()
        .WithSensor<EvidenceGroundingSensor>();

    if (usingRealModel)
        builder.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = anthropicKey!,
            ModelId = config["Anthropic:ModelId"] ?? "claude-sonnet-4-6"
        }));
    else
        builder.WithModel(_ => new FakeModelClient());
});

await using var provider = services.BuildServiceProvider();

var debtor = args.Length > 0 ? string.Join(" ", args) : "Marks and Spencer Group PLC";

Console.WriteLine($"Verifying debtor: {debtor}");
Console.WriteLine();

var outcome = await provider.GetRequiredService<Agent>()
    .RunAsync(
        $"Verify debtor: {debtor}",
        new Budget
        {
            MaxTurns = 30,
            MaxContextTokens = 150_000,
            MaxCost = 1.00m,
            MaxWallClock = TimeSpan.FromMinutes(5)
        });

Console.WriteLine();
Console.WriteLine(outcome.Status == AgentStatus.Done
    ? outcome.FinalAnswer
    : $"Agent did not complete. Status: {outcome.Status}. {outcome.FailureReason}");
