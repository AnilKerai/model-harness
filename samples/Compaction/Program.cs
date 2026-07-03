using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Samples.Common;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

// Runs the SAME 8-step investigation twice, changing only the compaction strategy, with a
// deliberately tiny context window so eviction fires most turns. Watch the printed compaction
// activity: the VIEW re-summarises the whole growing head every time; the FOLD only ever touches
// the newly evicted slice. No API key needed — the model and both strategies are scripted.

await RunScenario(
    "view",
    "Stateless view (like NullCompactionStrategy) — re-summarises the whole evicted head every turn.",
    b => b.WithCompactionStrategy<NarratingViewStrategy>());

await RunScenario(
    "fold",
    "Incremental fold (like AiCompactionStrategy) — summarises only the newly evicted slice.",
    b => b.WithCompactionStrategy<NarratingFoldStrategy>());

static async Task RunScenario(string name, string description, Action<ModelHarnessBuilder> registerCompaction)
{
    AgentConsoleWriter.PrintHeader($"compaction/{name}", description);

    var services = new ServiceCollection();
    services.AddStandardModelHarness(builder =>
    {
        builder
            .WithSystemPrompt("You are an investigation agent. Work through the subsystems, recording a finding at each step.")
            .WithModel(_ => new ScriptedInvestigator(steps: 8))
            .WithTool<RecordTool>();
        registerCompaction(builder);
    });

    await using var provider = services.BuildServiceProvider();
    var agent = provider.GetRequiredService<Agent>();

    // Tiny MaxContextTokens forces eviction. Model/compaction token usage is zero so this budget
    // stays purely about context-window size, not cumulative token spend (the two share the field).
    var budget = new StateBudget
    {
        MaxTurns = 30,
        MaxContextTokens = 240,
        MaxCost = 100m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };
    var state = AgentState.NewTask(
        "Investigate every subsystem and summarise the findings.", budget, TimeProvider.System.GetUtcNow());

    Console.WriteLine("Compaction activity (each line printed by the strategy as the harness evicts):");
    var outcome = await agent.RunAsync(state);

    var rolling = outcome.FinalState.RollingSummary;
    Console.WriteLine();
    Console.WriteLine($"Result                    : {outcome.Status}");
    Console.WriteLine($"Rolling summary persisted : {(rolling is null ? "(none — a view persists nothing across turns)" : $"covers {rolling.FoldedStepCount} step(s)")}");
    Console.WriteLine($"Compaction cost accrued   : {outcome.FinalState.CompactionCost:F4}  (counted against MaxCost by the budget enforcer)");
}

// ── Scripted model: N tool-calling turns of "research", then a final answer ──────────────────────
file sealed class ScriptedInvestigator(int steps) : IModelClient
{
    private int _turn;

    public Task<ModelResponse> CallAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _turn);

        if (n <= steps)
        {
            // Args vary per turn so the built-in StuckDetector doesn't flag repeated identical calls.
            var args = JsonDocument.Parse($$"""{"finding":"detail from subsystem {{n}}"}""").RootElement;
            return Task.FromResult(new ModelResponse
            {
                Text = $"Step {n}: I examined subsystem #{n} and found a noteworthy detail worth recording before moving on to the next area.",
                ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), "record", args)],
                StopReason = StopReason.ToolUse,
                Usage = Usage.Zero,
                Cost = 0m,
                Model = "scripted-investigator",
                Provider = "fake"
            });
        }

        return Task.FromResult(new ModelResponse
        {
            Text = "Investigation complete — I examined every subsystem and recorded the findings along the way.",
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = Usage.Zero,
            Cost = 0m,
            Model = "scripted-investigator",
            Provider = "fake"
        });
    }
}

file sealed class RecordTool : ITool
{
    public string Name => "record";
    public string Description => "Records a finding to the case file.";
    public JsonElement InputSchema => JsonDocument.Parse("""{"type":"object","properties":{"finding":{"type":"string"}}}""").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
        Task.FromResult(new ToolResult(call.CallId, "Finding recorded to the case file for later synthesis."));
}

// ── A stateless view: returns UpdatedSummary = null, so nothing persists and the whole growing
//    head is handed back every turn. Cost scales with head size — the problem the fold solves. ──────
file sealed class NarratingViewStrategy : ICompactionStrategy
{
    private int _pass;

    public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct)
    {
        _pass++;
        var head = request.EvictedSteps.Count;
        Console.WriteLine($"  [view  pass #{_pass}] re-summarising ALL {head} head step(s) from scratch (nothing was persisted last turn)");

        return Task.FromResult(new CompactionResult
        {
            InjectedText = $"[{head} earlier step(s) summarised]",
            UpdatedSummary = null,          // view: persist nothing → next turn starts over
            Cost = 0.0002m * head           // cost grows with the head — the "blocking model cost" problem
        });
    }
}

// ── An incremental fold: folds only the newly evicted slice onto the prior summary and persists the
//    result, so each pass touches a small, roughly constant number of steps at a flat cost. ──────────
file sealed class NarratingFoldStrategy : ICompactionStrategy
{
    private int _pass;

    public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct)
    {
        _pass++;
        var prior = request.PriorSummary?.FoldedStepCount ?? 0;
        var fresh = request.EvictedSteps.Count;
        var total = prior + fresh;
        Console.WriteLine($"  [fold  pass #{_pass}] prior summary covers {prior} step(s); folding {fresh} NEW step(s) → summary now covers {total}");

        var text = $"[Rolling summary of the first {total} step(s), folded across {_pass} pass(es).]";
        return Task.FromResult(new CompactionResult
        {
            InjectedText = text,
            UpdatedSummary = new RollingSummary(text, total),   // persisted → next turn folds onward
            Cost = 0.0002m                                      // flat per fold, regardless of run length
        });
    }
}
