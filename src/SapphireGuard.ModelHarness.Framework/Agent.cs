using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;
using AgentBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework;

[ExcludeFromCodeCoverage]
public sealed class Agent(HarnessLoop loop, TimeProvider timeProvider)
{
    private static readonly AgentBudget DefaultBudget = new()
    {
        MaxTurns = 10,
        MaxContextTokens = 100_000,
        MaxCost = 1.00m,
        MaxWallClock = TimeSpan.FromMinutes(2)
    };

    public Task<AgentOutcome> RunAsync(string taskText, AgentBudget? budget = null, CancellationToken ct = default) =>
        loop.RunAsync(AgentState.NewTask(taskText, budget ?? DefaultBudget, timeProvider.GetUtcNow()), ct);

    public Task<AgentOutcome> RunAsync(AgentState state, CancellationToken ct = default) =>
        loop.RunAsync(state, ct);
}
