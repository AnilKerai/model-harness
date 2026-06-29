using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Guides;

public sealed class DefaultGuideRunnerTests
{
    private static readonly StateBudget Budget = new()
    {
        MaxTurns = 10,
        MaxContextTokens = 1000,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    };

    private static AgentState State() =>
        AgentState.NewTask("t", Budget, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class RecordingGuide(string name, List<string> log) : IGuide
    {
        public string Name => name;
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            log.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTrajectoryGuide(List<string> log) : ITrajectoryGuide
    {
        public string Name => "trajectory";
        public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
        {
            log.Add(Name);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunsSupportingGuidesInRegistrationOrder_ThenTrajectoryGuideLast()
    {
        var log = new List<string>();
        var runner = new DefaultGuideRunner(
            [new RecordingGuide("a", log), new RecordingGuide("b", log), new RecordingGuide("c", log)],
            new RecordingTrajectoryGuide(log));

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "a", "b", "c", "trajectory" }, log);
    }

    [Fact]
    public async Task TrajectoryGuideRunsLast_EvenWithNoSupportingGuides()
    {
        var log = new List<string>();
        var runner = new DefaultGuideRunner([], new RecordingTrajectoryGuide(log));

        await runner.RunAsync(State(), [], CancellationToken.None);

        Assert.Equal(new[] { "trajectory" }, log);
    }
}
