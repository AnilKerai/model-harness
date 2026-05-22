using System.Globalization;
using ModelHarness.Framework.DependencyInjection;
using ModelHarness.Framework.Loop;
using ModelHarness.Framework.Sensors;
using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;
using ModelHarness.Infrastructure.Anthropic.Model;
using ModelHarness.Infrastructure.Tools;
using ModelHarness.Infrastructure.Tracing;
using ModelHarness.SampleAgent.Sensors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── Configuration ────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException(
        "Anthropic:ApiKey is empty. Set it in appsettings.local.json " +
        "with { \"Anthropic\": { \"ApiKey\": \"sk-ant-...\" } }.");

var anthropicOptions = new ClaudeClientOptions
{
    ApiKey = apiKey,
    ModelId = config["Anthropic:ModelId"] ?? "claude-sonnet-4-5-20251001"
};

// ── DI ───────────────────────────────────────────────────────────────────────

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

var services = new ServiceCollection();

services
    .AddModelHarness(SystemPrompt)
    .AddTracer<ConsoleTracer>()
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddModelClient(_ => new ClaudeModelClient(anthropicOptions));

services.AddSingleton<ITool, EchoTool>();
services.AddSingleton<ITool, CalculatorTool>();

services.AddSingleton<ISensor, ToolCallReasonablenessSensor>();
services.AddSingleton<ISensor, StuckDetector>();

await using var provider = services.BuildServiceProvider();

// ── Run ──────────────────────────────────────────────────────────────────────

var harness = provider.GetRequiredService<HarnessLoop>();

var state = AgentState.NewTask(
    taskText: "What is 124 multiplied by 37?",
    budget: new Budget
    {
        MaxTurns = 8,
        MaxContextTokens = 100_000,
        MaxCostUsd = 1.00m,
        MaxWallClock = TimeSpan.FromSeconds(60)
    });

using var cts = new CancellationTokenSource();
var outcome = await harness.RunAsync(state, cts.Token);

Console.WriteLine();
Console.WriteLine("=== Outcome ===");
Console.WriteLine($"TaskId: {outcome.TaskId}");
Console.WriteLine($"Status: {outcome.Status}");
Console.WriteLine($"FinalAnswer: {outcome.FinalAnswer}");
if (outcome.FailureReason is not null)
{
    Console.WriteLine($"FailureReason: {outcome.FailureReason}");
}

Console.WriteLine();
Console.WriteLine("=== Trajectory ===");
foreach (var step in outcome.FinalState.Trajectory)
{
    var line = step switch
    {
        ModelCallStep m => $"[model]  stop={m.Response.StopReason} tools={m.Response.ToolCalls.Count} cost=${m.Cost.ToString("F4", CultureInfo.InvariantCulture)} text=\"{m.Response.Text}\"",
        ToolCallStep t => $"[tool]   {t.Call.ToolName}({t.Call.Arguments.GetRawText()}) -> {(t.Result.IsError ? "ERROR: " : "")}{t.Result.Content}",
        SensorInterventionStep s => $"[sensor] {s.SensorName}@{s.HookPoint}: {s.Reason}",
        _ => $"[?] {step.GetType().Name}"
    };
    Console.WriteLine(line);
}
