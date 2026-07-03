using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class CriticSensorTests
{
    private static AgentState EmptyState() => AgentState.NewTask("summarise the report", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    private static ModelCallStep ModelStep(string? text) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [],
            Response: new ModelResponse
            {
                Text = text, ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m
            },
            Usage: Usage.Zero, Cost: 0m);

    private sealed class ScriptedModel(string text, Usage? usage = null, decimal cost = 0m) : IModelClient
    {
        public int Calls { get; private set; }

        public Task<ModelResponse> CallAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ModelResponse
            {
                Text = text, ToolCalls = [], StopReason = StopReason.EndTurn,
                Usage = usage ?? new Usage(10, 5), Cost = cost
            });
        }
    }

    private sealed class ThrowingModel : IModelClient
    {
        public int Calls { get; private set; }

        public Task<ModelResponse> CallAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> availableTools, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("model down");
        }
    }

    [Fact]
    public async Task ScoreBelowThreshold_ChallengesWithDeficiencies()
    {
        var model = new ScriptedModel("""{"score": 0.2, "deficiencies": ["missing the cost breakdown", "no source cited"]}""",
            new Usage(12, 8), 0.001m);
        var sut = new CriticSensor(model, passThreshold: 0.6);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("here is the summary"), CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("missing the cost breakdown", result.Reason);
        Assert.Equal(new Usage(12, 8), result.Usage);
        Assert.Equal(0.001m, result.Cost);
    }

    [Fact]
    public async Task ScoreAtOrAboveThreshold_PassesButPropagatesUsage()
    {
        var model = new ScriptedModel("""{"score": 0.9, "deficiencies": []}""", new Usage(12, 8), 0.001m);
        var sut = new CriticSensor(model, passThreshold: 0.6);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("a thorough summary"), CancellationToken.None);

        Assert.False(result.IsIntervene);
        Assert.Contains("0.90", result.Reason!);
        Assert.Equal(new Usage(12, 8), result.Usage);
        Assert.Equal(0.001m, result.Cost);
    }

    [Fact]
    public async Task ModelThrows_FailsOpenWithoutUsage()
    {
        var model = new ThrowingModel();
        var sut = new CriticSensor(model);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("answer"), CancellationToken.None);

        Assert.False(result.IsIntervene);
        Assert.Null(result.Usage);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task UnparseableResponse_FailsOpenWithUsage()
    {
        var model = new ScriptedModel("Honestly it looks pretty good to me", new Usage(5, 5), 0m);
        var sut = new CriticSensor(model);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("answer"), CancellationToken.None);

        Assert.False(result.IsIntervene);
        Assert.Contains("parse", result.Reason!);
        Assert.Equal(new Usage(5, 5), result.Usage);
    }

    [Fact]
    public async Task JsonWrappedInProse_IsParsed()
    {
        var model = new ScriptedModel("""Sure — here is my verdict: {"score": 0.1, "deficiencies": ["incomplete"]} Hope it helps!""");
        var sut = new CriticSensor(model, passThreshold: 0.6);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("answer"), CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("incomplete", result.Reason);
    }

    [Fact]
    public async Task NonModelCallTrigger_PassesWithoutCallingModel()
    {
        var model = new ThrowingModel();
        var sut = new CriticSensor(model);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), triggeringStep: null, CancellationToken.None);

        Assert.False(result.IsIntervene);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task EmptyAnswer_PassesWithoutCallingModel()
    {
        var model = new ThrowingModel();
        var sut = new CriticSensor(model);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep(""), CancellationToken.None);

        Assert.False(result.IsIntervene);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task CustomThreshold_IsRespected()
    {
        var model = new ScriptedModel("""{"score": 0.7, "deficiencies": ["needs more detail"]}""");
        var sut = new CriticSensor(model, passThreshold: 0.8);

        var result = await sut.CheckAsync(HookPoint.PreReturn, EmptyState(), ModelStep("answer"), CancellationToken.None);

        Assert.True(result.IsIntervene);
    }
}
