using System.Globalization;
using SapphireGuard.Framework.DependencyInjection;
using SapphireGuard.Framework.Loop;
using SapphireGuard.Framework.Sensors;
using SapphireGuard.Framework.State;
using SapphireGuard.Framework.Tools;
using SapphireGuard.Framework.Model;
using SapphireGuard.Infrastructure.Anthropic.Model;
using SapphireGuard.Infrastructure.Model;
using SapphireGuard.Infrastructure.Tools;
using SapphireGuard.Infrastructure.Tracing;
using SapphireGuard.SampleAgent.Sensors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── Configuration ────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

if (!usingRealModel)
    Console.WriteLine(
        "WARNING: Anthropic:ApiKey not set — falling back to FakeModelClient. " +
        "Add appsettings.local.json with { \"Anthropic\": { \"ApiKey\": \"sk-ant-...\" } } to use Claude.");

// ── DI ───────────────────────────────────────────────────────────────────────

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

var services = new ServiceCollection();

services
    .AddSapphireGuard(SystemPrompt)
    .AddTracer<ConsoleTracer>()
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddModelClient(_ =>
    {
        IModelClient inner = usingRealModel
            ? new ClaudeModelClient(new ClaudeClientOptions
            {
                ApiKey = apiKey!,
                ModelId = config["Anthropic:ModelId"] ?? "claude-sonnet-4-5-20251001"
            })
            : new FakeModelClient();

        return new PollyResilientModelClient(inner);
    });

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
