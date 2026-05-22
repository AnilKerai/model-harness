using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Output;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

public static class CheckpointResume
{
    private static readonly string CheckpointDir = Path.Combine(Path.GetTempPath(), "model-harness-checkpoints");

    public static async Task RunAsync(string systemPrompt, Action<IServiceCollection> configure, CancellationToken ct = default)
    {
        AgentConsoleWriter.PrintHeader(
            "checkpoint-resume",
            "Runs a task with FileCheckpointStore enabled, then loads the final checkpoint and resumes — proving at-least-once resume semantics.");

        var services = new ServiceCollection();
        services.AddModelHarness(systemPrompt);
        configure(services);
        services.AddFileCheckpointStore(CheckpointDir);

        await using var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<HarnessLoop>();

        var firstOutcome = await harness.RunAsync(
            AgentState.NewTask("What is 56 multiplied by 13?", Budget()), ct);

        AgentConsoleWriter.PrintOutcome(firstOutcome);

        await ResumeFromCheckpointAsync(systemPrompt, configure, firstOutcome, provider, ct);
    }

    private static async Task ResumeFromCheckpointAsync(
        string systemPrompt,
        Action<IServiceCollection> configure,
        AgentOutcome firstOutcome,
        IServiceProvider firstProvider,
        CancellationToken ct)
    {
        var store = firstProvider.GetRequiredService<ICheckpointStore>();
        var taskId = firstOutcome.TaskId;

        var files = Directory.GetFiles(
            Path.Combine(CheckpointDir, taskId), "*.json", SearchOption.TopDirectoryOnly);

        Console.WriteLine();
        Console.WriteLine($"Checkpoints written : {files.Length} file(s) → {Path.Combine(CheckpointDir, taskId)}");

        var latest = await store.LoadLatestAsync(taskId, ct);
        if (latest is null)
        {
            Console.WriteLine("ERROR: no checkpoint found after run.");
            return;
        }

        Console.WriteLine($"Latest checkpoint   : turn={latest.TurnNumber}, trajectory steps={latest.State.Trajectory.Count}");
        Console.WriteLine();
        Console.WriteLine("── Resuming from checkpoint ────────────────────────────────────");

        var resumedState = latest.State with
        {
            Status = AgentStatus.Running,
            Budget = firstOutcome.FinalState.Budget
        };

        var resumeServices = new ServiceCollection();
        resumeServices.AddModelHarness(systemPrompt);
        resumeServices.AddFileCheckpointStore(CheckpointDir);
        configure(resumeServices);

        var sharedModelClient = firstProvider.GetRequiredService<Framework.Model.IModelClient>();
        var sharedToolRegistry = firstProvider.GetRequiredService<Framework.Tools.IToolRegistry>();
        var sharedTracer = firstProvider.GetRequiredService<Framework.Tracing.ITracer>();
        resumeServices.AddModelClient(_ => sharedModelClient);
        resumeServices.AddToolRegistry(_ => sharedToolRegistry);
        resumeServices.AddTracer(_ => sharedTracer);

        await using var resumeProvider = resumeServices.BuildServiceProvider();
        var resumeOutcome = await resumeProvider.GetRequiredService<HarnessLoop>()
            .RunAsync(resumedState, ct);

        Console.WriteLine($"Resume status       : {resumeOutcome.Status}");
        Console.WriteLine($"Resume answer       : {resumeOutcome.FinalAnswer ?? "(none)"}");
    }

    private static Budget Budget() => new()
    {
        MaxTurns = 3,
        MaxContextTokens = 100_000,
        MaxCostUsd = 1.00m,
        MaxWallClock = TimeSpan.FromSeconds(60)
    };
}
