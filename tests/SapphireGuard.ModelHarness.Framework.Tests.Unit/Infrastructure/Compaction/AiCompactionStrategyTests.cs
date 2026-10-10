using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Compaction;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Compaction;

public sealed class AiCompactionStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RollingSummary Prior = new("[Summary of 3 earlier step(s)] looked up the account", 3);

    private static CompactionRequest Request(RollingSummary? prior) => new()
    {
        State = AgentState.NewTask("t", new StateBudget
        {
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, Now),
        EvictedSteps = [new UserMessageStep(Guid.NewGuid(), Now, "and the second account?")],
        PriorSummary = prior,
        RemainingTokenBudget = 1_000
    };

    private static ModelResponse Summary(string? text) => new()
    {
        Text = text, ToolCalls = [], StopReason = StopReason.EndTurn, Usage = new Usage(40, 12), Cost = 0.002m
    };

    private sealed class ThrowingModel(Exception exception) : IModelClient
    {
        public Task<ModelResponse> CallAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
            => throw exception;
    }

    [Fact]
    public async Task Success_FoldsTheNewSliceOntoThePriorSummary_WithNoError()
    {
        var sut = new AiCompactionStrategy(new ScriptedModelClient(Summary("looked up both accounts")));

        var result = await sut.CompactAsync(Request(Prior), CancellationToken.None);

        Assert.Equal(4, result.UpdatedSummary!.FoldedStepCount);
        Assert.Contains("looked up both accounts", result.InjectedText);
        Assert.Equal(new Usage(40, 12), result.Usage);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ModelThrows_KeepsThePriorSummaryAndReportsWhy()
    {
        var sut = new AiCompactionStrategy(new ThrowingModel(new InvalidOperationException("model down")));

        var result = await sut.CompactAsync(Request(Prior), CancellationToken.None);

        // Watermark not advanced — the guide re-evicts the slice and retries it next turn.
        Assert.Equal(Prior, result.UpdatedSummary);
        Assert.Contains(Prior.Text, result.InjectedText);
        Assert.Equal("InvalidOperationException: model down", result.Error);
    }

    [Fact]
    public async Task ModelTimesOutOnItsOwn_FailsOpenLikeAnyOtherFault()
    {
        // An HttpClient timeout surfaces as TaskCanceledException with the harness token untouched.
        var sut = new AiCompactionStrategy(new ThrowingModel(new TaskCanceledException("request timed out")));

        var result = await sut.CompactAsync(Request(Prior), CancellationToken.None);

        Assert.Equal(Prior, result.UpdatedSummary);
        Assert.Equal("TaskCanceledException: request timed out", result.Error);
    }

    [Fact]
    public async Task HarnessCancellationRequested_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = new AiCompactionStrategy(new ScriptedModelClient(Summary("unused")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.CompactAsync(Request(Prior), cts.Token));
    }

    [Fact]
    public async Task EmptySummary_FailsOpenAndReportsWhy()
    {
        var sut = new AiCompactionStrategy(new ScriptedModelClient(Summary("  ")));

        var result = await sut.CompactAsync(Request(prior: null), CancellationToken.None);

        Assert.Null(result.UpdatedSummary);
        Assert.Contains("omitted", result.InjectedText);
        Assert.Contains("empty summary", result.Error);
    }

    [Fact]
    public async Task EmptySummary_StillChargesTheCallThatProducedIt()
    {
        // The model was called and billed; a useless answer must not make that spend disappear
        // from the run's budget.
        var sut = new AiCompactionStrategy(new ScriptedModelClient(Summary(null)));

        var result = await sut.CompactAsync(Request(prior: null), CancellationToken.None);

        Assert.Equal(new Usage(40, 12), result.Usage);
        Assert.Equal(0.002m, result.Cost);
    }
}
