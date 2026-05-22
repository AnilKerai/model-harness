using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.Samples.Common;

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

var checkpointDir = Path.Combine(Path.GetTempPath(), "model-harness-checkpoints");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

if (!usingRealModel)
    Console.WriteLine("WARNING: Anthropic:ApiKey not configured — using FakeModelClient.");

IModelClient BuildModelClient() =>
    usingRealModel
        ? new ResilientModelClientDecorator(new ClaudeModelClient(new ClaudeClientOptions
            { ApiKey = apiKey!, ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5" }))
        : new FakeModelClient();

// ── First run ─────────────────────────────────────────────────────────────────

var firstServices = new ServiceCollection();

firstServices.AddModelHarness(SystemPrompt);
firstServices
    .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddSingleton<ITool, EchoTool>()
    .AddSingleton<ITool, CalculatorTool>()
    .AddFileCheckpointStore(checkpointDir)
    .AddModelClient(_ => BuildModelClient());

await using var firstProvider = firstServices.BuildServiceProvider();

AgentConsoleWriter.PrintHeader(
    "checkpoint-resume",
    "Runs the task with FileCheckpointStore, then resumes from the latest checkpoint — proving at-least-once resume semantics.");

var firstOutcome = await firstProvider.GetRequiredService<Agent>()
    .RunAsync("What is 56 multiplied by 13?",
        budget: new Budget { MaxTurns = 3, MaxContextTokens = 100_000, MaxCostUsd = 1.00m, MaxWallClock = TimeSpan.FromSeconds(60) });

AgentConsoleWriter.PrintOutcome(firstOutcome);

// ── Resume ────────────────────────────────────────────────────────────────────

var store = firstProvider.GetRequiredService<ICheckpointStore>();
var taskId = firstOutcome.TaskId;

var files = Directory.GetFiles(Path.Combine(checkpointDir, taskId), "*.json", SearchOption.TopDirectoryOnly);
Console.WriteLine();
Console.WriteLine($"Checkpoints written : {files.Length} file(s) → {Path.Combine(checkpointDir, taskId)}");

var latest = await store.LoadLatestAsync(taskId, CancellationToken.None);
if (latest is null)
{
    Console.WriteLine("ERROR: no checkpoint found after run.");
    return;
}

Console.WriteLine($"Latest checkpoint   : turn={latest.TurnNumber}, trajectory steps={latest.State.Trajectory.Count}");
Console.WriteLine();
Console.WriteLine("── Resuming from checkpoint ────────────────────────────────────");

var resumedState = latest.State with { Status = AgentStatus.Running, Budget = firstOutcome.FinalState.Budget };

var resumeServices = new ServiceCollection();
resumeServices.AddModelHarness(SystemPrompt);
resumeServices
    .AddTracer(_ => firstProvider.GetRequiredService<ITracer>())
    .AddToolRegistry(_ => firstProvider.GetRequiredService<IToolRegistry>())
    .AddModelClient(_ => firstProvider.GetRequiredService<IModelClient>())
    .AddFileCheckpointStore(checkpointDir);

await using var resumeProvider = resumeServices.BuildServiceProvider();

var resumeOutcome = await resumeProvider.GetRequiredService<Agent>()
    .RunAsync(resumedState);

Console.WriteLine($"Resume status       : {resumeOutcome.Status}");
Console.WriteLine($"Resume answer       : {resumeOutcome.FinalAnswer ?? "(none)"}");
