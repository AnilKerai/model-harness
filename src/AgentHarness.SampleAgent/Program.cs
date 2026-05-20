using System.Globalization;
using AgentHarness.Framework.Budget;
using AgentHarness.Framework.Context;
using AgentHarness.Framework.Loop;
using AgentHarness.Framework.Model;
using AgentHarness.Framework.Sensors;
using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;
using AgentHarness.Framework.Tracing;
using AgentHarness.Infrastructure.Model;
using AgentHarness.Infrastructure.Tools;
using AgentHarness.Infrastructure.Tracing;
using AgentHarness.SampleAgent.Sensors;
using Microsoft.Extensions.DependencyInjection;

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

var services = new ServiceCollection();

services.AddSingleton<ITool, EchoTool>();
services.AddSingleton<ITool, CalculatorTool>();
services.AddSingleton<IToolRegistry>(sp => new InMemoryToolRegistry(sp.GetRequiredService<IEnumerable<ITool>>()));

services.AddSingleton<ISensor, ToolCallReasonablenessSensor>();
services.AddSingleton<ISensor, StuckDetector>();
services.AddSingleton<ISensorRunner>(sp => new DefaultSensorRunner(sp.GetRequiredService<IEnumerable<ISensor>>()));

services.AddSingleton<IToolSelector, PassthroughToolSelector>();
services.AddSingleton<ITrajectoryCompactor, NoopTrajectoryCompactor>();
services.AddSingleton<IMemoryRetriever, NoopMemoryRetriever>();
services.AddSingleton<IContextBuilder>(sp => new DefaultContextBuilder(
    SystemPrompt,
    sp.GetRequiredService<IToolSelector>(),
    sp.GetRequiredService<ITrajectoryCompactor>(),
    sp.GetRequiredService<IMemoryRetriever>()));

services.AddSingleton<IBudgetEnforcer, DefaultBudgetEnforcer>();
services.AddSingleton<ITracer, ConsoleTracer>();

services.AddSingleton<IModelClient>(_ => new PollyResilientModelClient(new FakeModelClient()));

services.AddSingleton<HarnessLoop>();

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
