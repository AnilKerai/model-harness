using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.MultiAgent;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.MultiAgent;

// What the parent agent sees for each way a delegated run can end. A sub-agent that did not finish must never
// reach the parent as a clean result, or the parent reasons on an answer it believes is complete.
public sealed class AgentToolStatusTests
{
    private static readonly ToolContext Ctx =
        ToolContext.Empty(Guid.NewGuid().ToString("n"), Guid.NewGuid().ToString("n"));

    private static ToolCall SubTaskCall() =>
        new(Guid.NewGuid().ToString("n"), "sub-agent", JsonDocument.Parse("""{"task":"triage"}""").RootElement);

    private static ModelResponse Response(string? text, StopReason stopReason, params ToolCall[] calls) => new()
    {
        Text = text,
        ToolCalls = calls,
        StopReason = stopReason,
        Usage = Usage.Zero,
        Cost = 0m
    };

    private static AgentTool BuildTool(IToolRegistry? subAgentTools, params ModelResponse[] responses)
    {
        var factory = new AgentFactory();
        factory.AddAgent("sub-agent", builder =>
        {
            builder
                .WithSystemPrompt("You are a sub-agent.")
                .WithModel(_ => new ScriptedModelClient(responses));
            if (subAgentTools is not null)
                builder.WithToolRegistry(_ => subAgentTools);
        });
        return new AgentTool("sub-agent", factory);
    }

    [Fact]
    public async Task Done_ReturnsTheAnswerAsAResult()
    {
        var sut = BuildTool(null, Response("all clear", StopReason.EndTurn));

        var result = await sut.ExecuteAsync(SubTaskCall(), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("all clear", result.Content);
    }

    [Fact]
    public async Task PartialResultWithAnAnswer_KeepsTheAnswerAndAppendsWhyItIsIncomplete()
    {
        var sut = BuildTool(null, Response("the first two emails are", StopReason.MaxTokens));

        var result = await sut.ExecuteAsync(SubTaskCall(), Ctx, CancellationToken.None);

        Assert.False(result.IsError); // a partial answer is still usable data
        Assert.StartsWith("the first two emails are", result.Content);
        Assert.Contains("[INCOMPLETE", result.Content);
        Assert.Contains("output-token limit", result.Content);
    }

    [Fact]
    public async Task PartialResultWithNoAnswer_IsAnError()
    {
        var sut = BuildTool(null, Response(null, StopReason.Refusal));

        var result = await sut.ExecuteAsync(SubTaskCall(), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("without an answer", result.Content);
        Assert.Contains("declined", result.Content);
    }

    [Fact]
    public async Task Failed_IsAnErrorCarryingTheReason()
    {
        // No scripted responses: the sub-agent's model call throws, so the run fails.
        var sut = BuildTool(null);

        var result = await sut.ExecuteAsync(SubTaskCall(), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("ran out of responses", result.Content);
    }

    [Fact]
    public async Task AwaitingHuman_IsAnErrorCarryingTheQuestion_NotAnEmptySuccess()
    {
        var askHuman = new StubToolRegistry(call =>
            new ToolResult(call.CallId, "Which account is this about?", IsPending: true), "ask_human");
        var call = new ToolCall("c1", "ask_human", JsonDocument.Parse("{}").RootElement);
        var sut = BuildTool(askHuman, Response(null, StopReason.ToolUse, call));

        var result = await sut.ExecuteAsync(SubTaskCall(), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("ask a human", result.Content);
        Assert.Contains("Which account is this about?", result.Content);
    }
}
