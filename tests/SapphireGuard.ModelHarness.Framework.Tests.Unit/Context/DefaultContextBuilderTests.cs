using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Context;

public sealed class DefaultContextBuilderTests
{
    private static AgentState State(string task = "do the thing") =>
        AgentState.NewTask(task, new StateBudget
        {
            MaxTurns = 10, MaxContextTokens = 100_000, MaxCostUsd = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        });

    private static DefaultContextBuilder BuilderWith(
        string systemPrompt = "You are helpful.",
        IReadOnlyList<string>? memories = null,
        IReadOnlyList<ITool>? tools = null)
    {
        var draft = new ContextDraft
        {
            SystemPrompt = systemPrompt,
            AvailableTools = tools?.ToList() ?? []
        };
        if (memories != null)
            draft.MemorySnippets.AddRange(memories);

        var runner = new FixedDraftGuideRunner(draft);
        return new DefaultContextBuilder(runner);
    }

    [Fact]
    public async Task BuildAsync_TaskTextIsLastUserMessage()
    {
        var builder = BuilderWith();
        var result = await builder.BuildAsync(State("my task"), [], CancellationToken.None);

        var last = result.Messages[^1];
        Assert.Equal(MessageRole.User, last.Role);
        Assert.Equal("my task", last.Content);
    }

    [Fact]
    public async Task BuildAsync_FirstMessageIsSystemPrompt()
    {
        var builder = BuilderWith(systemPrompt: "Be precise.");
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        var first = result.Messages[0];
        Assert.Equal(MessageRole.System, first.Role);
        Assert.Contains("Be precise.", first.Content);
    }

    [Fact]
    public async Task BuildAsync_MemorySnippetsAppearsInSystemMessage()
    {
        var builder = BuilderWith(memories: ["fact A", "fact B"]);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        var system = result.Messages[0].Content;
        Assert.Contains("fact A", system);
        Assert.Contains("fact B", system);
    }

    [Fact]
    public async Task BuildAsync_NoMemory_SystemMessageHasNoMemorySection()
    {
        var builder = BuilderWith(memories: []);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        Assert.DoesNotContain("Relevant memory", result.Messages[0].Content);
    }

    [Fact]
    public async Task BuildAsync_AvailableToolsListedInSystemMessage()
    {
        var tool = new StubTool("calc", "Does maths");
        var builder = BuilderWith(tools: [tool]);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        var system = result.Messages[0].Content;
        Assert.Contains("calc", system);
        Assert.Contains("Does maths", system);
    }

    [Fact]
    public async Task BuildAsync_NoTools_SystemMessageHasNoToolsSection()
    {
        var builder = BuilderWith(tools: []);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        Assert.DoesNotContain("Available tools", result.Messages[0].Content);
    }

    [Fact]
    public async Task BuildAsync_SelectedToolsReturnedInResult()
    {
        var tool = new StubTool("echo", "Echoes");
        var builder = BuilderWith(tools: [tool]);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        Assert.Single(result.SelectedTools, t => t.Name == "echo");
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

file sealed class FixedDraftGuideRunner(ContextDraft draft) : IGuideRunner
{
    public Task<ContextDraft> RunAsync(AgentState state, IReadOnlyList<ITool> allTools, CancellationToken ct)
        => Task.FromResult(draft);
}

file sealed class StubTool(string name, string description) : ITool
{
    public string Name => name;
    public string Description => description;
    public System.Text.Json.JsonElement InputSchema =>
        System.Text.Json.JsonDocument.Parse("{}").RootElement;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
        => Task.FromResult(new ToolResult(call.CallId, "ok"));
}
