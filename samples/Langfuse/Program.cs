using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.Samples.Common;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

AgentConsoleWriter.PrintHeader(
    "langfuse",
    "A two-turn chat exported to Langfuse over OTLP. Each turn is one trace (agent → generations and " +
    "tool calls); the shared task id groups both turns into one Langfuse session.");

// Langfuse's own variable names win over appsettings (and Langfuse__* variables), so keys never have to be
// written into the repo.
string? Setting(string env, string key) => Environment.GetEnvironmentVariable(env) ?? config[key];
var publicKey = Setting("LANGFUSE_PUBLIC_KEY", "Langfuse:PublicKey");
var secretKey = Setting("LANGFUSE_SECRET_KEY", "Langfuse:SecretKey");
var baseUrl = Setting("LANGFUSE_BASE_URL", "Langfuse:BaseUrl")!.TrimEnd('/');
var exportToLangfuse = !string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(secretKey);

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

if (!exportToLangfuse)
    Console.WriteLine("WARNING: Langfuse keys not configured — the agent runs, but no traces are exported.");
if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using FakeModelClient.");

var tracing = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(r => r
        .AddService("model-harness-langfuse-sample")
        // Langfuse reads this as the trace environment, so development runs stay out of production views.
        .AddAttributes([new("deployment.environment.name", config["Langfuse:Environment"] ?? "development")]))
    // Without this the harness's ActivitySource has no listener and no span is ever created.
    .AddSource(OpenTelemetryTracer.ActivitySourceName);

if (exportToLangfuse)
    tracing.AddOtlpExporter(o =>
    {
        // An endpoint set in code is used as-is, so it names the traces path. Langfuse takes OTLP over HTTP only.
        o.Endpoint = new Uri($"{baseUrl}/api/public/otel/v1/traces");
        o.Protocol = OtlpExportProtocol.HttpProtobuf;
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));
        // Ingestion version 4 shows spans in real time; without it they can take up to 10 minutes to appear.
        o.Headers = $"Authorization=Basic {auth},x-langfuse-ingestion-version=4";
    });

using var tracerProvider = tracing.Build();

var services = new ServiceCollection();

services.AddStandardChatHarness(builder =>
{
    builder
        .WithSystemPrompt("You are an arithmetic assistant. Use the calculator for every calculation, then answer in one sentence.")
        // Reconfigures the tracer the standard harness already adds. The agent name becomes the Langfuse trace
        // name. Content capture puts prompts, answers and tool I/O on the trace — everything in the conversation
        // leaves the process, so keep it to development.
        .WithOtelTracer(enableSensitiveData: true, agentName: "arithmetic-assistant")
        .WithTool<CalculatorTool>();

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
var agent = provider.GetRequiredService<Agent>();
var timeProvider = provider.GetRequiredService<TimeProvider>();

var budget = new Budget
{
    MaxTurns = 6,
    MaxTotalTokens = 50_000,
    MaxCost = 0.50m,
    MaxWallClock = TimeSpan.FromMinutes(2)
};

string[] userTurns = ["What is 124 multiplied by 37?", "Now divide that by 4."];

AgentOutcome? outcome = null;
foreach (var message in userTurns)
{
    var now = timeProvider.GetUtcNow();
    var state = outcome is null
        ? AgentState.NewTask(message, budget, now)
        : outcome.FinalState.WithUserMessage(message, now);

    outcome = await agent.RunAsync(state);

    Console.WriteLine($"you>   {message}");
    Console.WriteLine($"agent> {outcome.FinalAnswer ?? "(no answer)"}");
    if (outcome.Status != AgentStatus.Done)
        Console.WriteLine($"       [status: {outcome.Status}{(outcome.FailureReason is { } why ? $" — {why}" : "")}]");
    Console.WriteLine();
}

// The batch exporter sends on a timer, and a console app can exit before its next tick.
tracerProvider.ForceFlush();

if (exportToLangfuse)
    Console.WriteLine($"Exported to {baseUrl}. In Langfuse, open Sessions and find session id {outcome!.TaskId}.");
