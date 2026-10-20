using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Persistence;

public sealed class CheckpointSizeTests : IDisposable
{
    private const int Turns = 50;
    private const string PromptMarker = "RENDERED-PROMPT-FILLER";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ckptsize_" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Every turn renders a prompt that grows with the trajectory, as a real context does until it
    // reaches the window. If ModelCallStep.Prompt were persisted, checkpoint k would hold k prompts.
    private sealed class GrowingPromptContextBuilder : IContextBuilder
    {
        public Task<ContextBuildResult> BuildAsync(AgentState state, IReadOnlyList<ITool> tools, CancellationToken ct)
        {
            var filler = string.Concat(Enumerable.Repeat(PromptMarker, 50));
            var messages = state.Trajectory.Select(_ => new Message(MessageRole.User, filler)).ToList();
            messages.Add(new Message(MessageRole.User, state.TaskText));
            return Task.FromResult(new ContextBuildResult(messages, tools));
        }
    }

    // A strictly increasing clock: checkpoint filenames carry CreatedAt to the millisecond, and a
    // fast scripted run would otherwise stamp several checkpoints with the same one.
    private sealed class TickingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    [Fact]
    public async Task FiftyTurnRun_LastCheckpointStaysSmall_AndCarriesNoRenderedPrompt()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        var script = Enumerable.Range(0, Turns - 1)
            .Select(i => new ModelResponse
            {
                Text = null,
                ToolCalls = [new ToolCall($"call-{i}", "noop", args)],
                StopReason = StopReason.ToolUse,
                Usage = Usage.Zero,
                Cost = 0m
            })
            .Append(new ModelResponse
            {
                Text = "done", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m
            })
            .ToArray();
        var store = new FileCheckpointStore(_dir);
        var harness = new HarnessLoop(
            modelClient: new ScriptedModelClient(script),
            toolRegistry: new StubToolRegistry(),
            contextBuilder: new GrowingPromptContextBuilder(),
            sensorRunner: new DefaultSensorRunner([]),
            budgetEnforcer: new AlwaysOkBudgetEnforcer(),
            rateLimiter: new NullRateLimiter(),
            tracer: new NullTracer(),
            checkpointStore: store,
            timeProvider: new TickingClock());
        var state = AgentState.NewTask("long task", new StateBudget
        {
            MaxTurns = Turns + 10, MaxTotalTokens = 1_000_000, MaxCost = 10m, MaxWallClock = TimeSpan.FromHours(1)
        }, DateTimeOffset.UtcNow);

        var outcome = await harness.RunAsync(state, CancellationToken.None);

        Assert.Equal(AgentStatus.Done, outcome.Status);
        Assert.Equal(Turns, outcome.FinalState.Trajectory.OfType<ModelCallStep>().Count());
        // The rendered prompts really did grow: the last one is far larger than the bound below,
        // so a persisted prompt would blow through it many times over.
        var lastPromptChars = outcome.FinalState.Trajectory.OfType<ModelCallStep>().Last().Prompt.Sum(m => m.Content.Length);
        Assert.True(lastPromptChars > 100_000, $"last prompt was only {lastPromptChars} chars");

        var files = Directory.GetFiles(Path.Combine(_dir, state.TaskId), "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.Equal(Turns, files.Length);
        var lastFile = files[^1];
        Assert.DoesNotContain(PromptMarker, await File.ReadAllTextAsync(lastFile));
        // Roughly 0.4 KB per step on disk, so about 40 KB for 50 turns; with prompts it was over 2 MB.
        Assert.True(new FileInfo(lastFile).Length < 64 * 1024, $"last checkpoint was {new FileInfo(lastFile).Length} bytes");

        // Linear, not quadratic: five times the turns costs about five times the bytes, where
        // persisted prompts cost about twenty-five times.
        var turn10 = new FileInfo(files[9]).Length;
        var turn50 = new FileInfo(lastFile).Length;
        Assert.True(turn50 < 6 * turn10, $"turn 10 checkpoint was {turn10} bytes, turn 50 was {turn50}");

        var latest = await store.LoadLatestAsync(state.TaskId);
        Assert.Equal(Turns - 1, latest!.TurnNumber);
    }
}
