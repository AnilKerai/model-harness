using System.Globalization;
using AgentHarness.Framework.DependencyInjection;
using AgentHarness.Framework.Loop;
using AgentHarness.Framework.Sensors;
using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;
using AgentHarness.Infrastructure.Model;
using AgentHarness.Infrastructure.Tools;
using AgentHarness.Infrastructure.Tracing;
using AgentHarness.SampleAgent.Sensors;
using Microsoft.Extensions.DependencyInjection;

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

var services = new ServiceCollection();

services
    .AddAgentHarness(SystemPrompt)
    .AddTracer<ConsoleTracer>()
    .AddToolRegistry<InMemoryToolRegistry>()
    .AddModelClient(_ => new PollyResilientModelClient(new FakeModelClient()));

services.AddSingleton<ITool, EchoTool>();
services.AddSingleton<ITool, CalculatorTool>();

services.AddSingleton<ISensor, ToolCallReasonablenessSensor>();
services.AddSingleton<ISensor, StuckDetector>();

await using var provider = services.BuildServiceProvider();

var harness = provider.GetRequiredService<HarnessLoop>();

var state = AgentState.NewTask(
    taskText: "What is twelve multiplied by seven?",
    budget: new Budget
    {
        MaxTurns = 8,
        MaxContextTokens = 100_000,
        MaxCostUsd = 1.00m,
        MaxWallClock = TimeSpan.FromSeconds(30)
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
