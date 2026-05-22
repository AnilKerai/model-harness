using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Ollama;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.SampleAgent.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

/// <summary>
/// All demo scenarios. Add a new <see cref="Scenario"/> entry here to include
/// it in the run — no other files need to change.
/// </summary>
public static class ScenarioLibrary
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        // ── 1. Happy path ─────────────────────────────────────────────────────
        new Scenario(
            Name: "happy-path",
            Description: "Normal arithmetic — all sensors should pass.",
            TaskText: "What is 124 multiplied by 37?"),

        // ── 2. PII detection ──────────────────────────────────────────────────
        new Scenario(
            Name: "pii-detection",
            Description: "Model is asked to echo back a user's email address. " +
                         "PiiRedactionSensor should block the response.",
            TaskText: "The user's email address is john.smith@acmecorp.com. " +
                      "Calculate 124 multiplied by 37, then address the user by " +
                      "their email address when presenting the result.",
            ConfigureSensors: services =>
            {
                services.AddSingleton<ISensor, PiiRedactionSensor>();
                services.AddSingleton<ISensor, StuckDetector>();
            }),

        // ── 3. Cost throttle ──────────────────────────────────────────────────
        new Scenario(
            Name: "cost-throttle",
            Description: "Soft spend cap set below the cost of one model call. " +
                         "CostThrottleSensor should fire before the second call.",
            TaskText: "What is 124 multiplied by 37?",
            ConfigureSensors: services =>
            {
                services.AddSingleton<ISensor>(_ => new CostThrottleSensor(softLimitUsd: 0.0005m));
                services.AddSingleton<ISensor, StuckDetector>();
            }),

        // ── 4. Tool call reasonableness ───────────────────────────────────────
        new Scenario(
            Name: "tool-call-reasonableness",
            Description: "Model is given a task that invites division by zero. " +
                         "ToolCallReasonablenessSensor should block the call before it dispatches.",
            TaskText: "What is 100 divided by 0?",
            ConfigureSensors: services =>
            {
                services.AddSingleton<ISensor, ToolCallReasonablenessSensor>();
                services.AddSingleton<ISensor, StuckDetector>();
            }),

        // ── 5. Tool result sanity ─────────────────────────────────────────────
        new Scenario(
            Name: "tool-result-sanity",
            Description: "A business-rule validator rejects calculator results above 1000. " +
                         "124 × 37 = 4588, which exceeds the limit — ToolResultSanityCheckSensor should block.",
            TaskText: "What is 124 multiplied by 37?",
            ConfigureSensors: services =>
            {
                services.AddSingleton<ISensor, StuckDetector>();
                services.AddSingleton<ISensor>(sp => new ToolResultSanityCheckSensor(
                    toolValidators: new Dictionary<string, Func<string, string?>>
                    {
                        ["calculator"] = result =>
                            double.TryParse(result, out var value) && value > 1000
                                ? $"result {value} exceeds the maximum allowed value of 1000"
                                : null
                    }));
            }),

        // ── 6. Checkpoint / resume ────────────────────────────────────────────
        BuildCheckpointResumeScenario(),
    ];

    public static Scenario BuildOllamaScenario(OllamaClientOptions options) =>
        new(
            Name: "ollama-tool-call",
            Description: "Runs a tool-calling task through a local Ollama model. " +
                         "Demonstrates ToolUse/Tool message grouping for Ollama's single-assistant-message format.",
            TaskText: "What is 56 multiplied by 13?",
            ConfigureSensors: services =>
            {
                services.AddOllamaModelClient(options);
                services.AddSingleton<ISensor, StuckDetector>();
            });

    private static Scenario BuildCheckpointResumeScenario()
    {
        var checkpointDir = Path.Combine(Path.GetTempPath(), "model-harness-checkpoints");

        return new Scenario(
            Name: "checkpoint-resume",
            Description: "Runs a task with FileCheckpointStore enabled, then loads the final " +
                         "checkpoint and feeds it to a second harness run — proving that a " +
                         "resumed agent produces the same answer from a serialised state.",
            TaskText: "What is 56 multiplied by 13?",
            Budget: new Budget
            {
                MaxTurns = 3,
                MaxContextTokens = 100_000,
                MaxCostUsd = 1.00m,
                MaxWallClock = TimeSpan.FromSeconds(60)
            },
            ConfigureSensors: services =>
                services.AddFileCheckpointStore(checkpointDir),
            PostRun: async (firstOutcome, firstProvider, ct) =>
            {
                var store = firstProvider.GetRequiredService<ICheckpointStore>();
                var taskId = firstOutcome.TaskId;

                var files = Directory.GetFiles(
                    Path.Combine(checkpointDir, taskId), "*.json",
                    SearchOption.TopDirectoryOnly);

                Console.WriteLine();
                Console.WriteLine($"Checkpoints written : {files.Length} file(s) → {Path.Combine(checkpointDir, taskId)}");

                var latest = await store.LoadLatestAsync(taskId, ct);
                if (latest is null)
                {
                    Console.WriteLine("ERROR: no checkpoint found after run.");
                    return;
                }

                Console.WriteLine($"Latest checkpoint   : turn={latest.TurnNumber}, " +
                                  $"trajectory steps={latest.State.Trajectory.Count}");
                Console.WriteLine();
                Console.WriteLine("── Resuming from checkpoint ────────────────────────────────────");

                // Resume: resume the task using the last checkpointed state.
                // Use a fresh harness (simulating a process restart) with the same
                // checkpoint store so resumed checkpoints are also durably saved.
                var resumedState = latest.State with
                {
                    Status = AgentStatus.Running,
                    Budget = firstOutcome.FinalState.Budget
                };

                var resumeServices = new ServiceCollection();
                resumeServices.AddModelHarness("You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.");
                resumeServices.AddFileCheckpointStore(checkpointDir);

                // Reuse the same model client and tool registry from the original provider.
                var sharedModelClient = firstProvider.GetRequiredService<Framework.Model.IModelClient>();
                var sharedToolRegistry = firstProvider.GetRequiredService<Framework.Tools.IToolRegistry>();
                var sharedTracer = firstProvider.GetRequiredService<Framework.Tracing.ITracer>();
                resumeServices.AddModelClient(_ => sharedModelClient);
                resumeServices.AddToolRegistry(_ => sharedToolRegistry);
                resumeServices.AddTracer(_ => sharedTracer);

                await using var resumeProvider = resumeServices.BuildServiceProvider();
                var resumeHarness = resumeProvider.GetRequiredService<HarnessLoop>();
                var resumeOutcome = await resumeHarness.RunAsync(resumedState, ct);

                Console.WriteLine($"Resume status       : {resumeOutcome.Status}");
                Console.WriteLine($"Resume answer       : {resumeOutcome.FinalAnswer ?? "(none)"}");
            });
    }
}
