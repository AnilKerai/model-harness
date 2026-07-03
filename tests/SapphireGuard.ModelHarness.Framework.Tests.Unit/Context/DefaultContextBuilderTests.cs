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
            MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
            MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

    private static DefaultContextBuilder BuilderWith(
        string systemPrompt = "You are helpful.",
        IReadOnlyList<string>? memories = null,
        IReadOnlyList<ITool>? tools = null,
        IReadOnlyList<string>? systemSections = null,
        IReadOnlyList<Message>? trajectoryMessages = null)
    {
        var draft = new ContextDraft
        {
            SystemPrompt = systemPrompt,
            AvailableTools = tools?.ToList() ?? []
        };
        if (memories != null)
            draft.MemorySnippets.AddRange(memories);
        if (systemSections != null)
            draft.SystemSections.AddRange(systemSections);
        if (trajectoryMessages != null)
            draft.TrajectoryMessages.AddRange(trajectoryMessages);

        var runner = new FixedDraftGuideRunner(draft);
        return new DefaultContextBuilder(runner);
    }

    [Fact]
    public async Task BuildAsync_AppendsTrajectoryMessagesAfterSystemMessage()
    {
        // The builder no longer pins the task as a trailing user message — user turns
        // (including the initial task) arrive via the trajectory guide as trajectory messages.
        var builder = BuilderWith(trajectoryMessages: [new Message(MessageRole.User, "my task")]);
        var result = await builder.BuildAsync(State("my task"), [], CancellationToken.None);

        Assert.Equal(MessageRole.System, result.Messages[0].Role);
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
    public async Task BuildAsync_SystemSectionsAppearInSystemMessage()
    {
        var builder = BuilderWith(systemSections: ["# Available tools\n- calc: Does maths", "# Available skills\n- deploy — shipping"]);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        var system = result.Messages[0].Content;
        Assert.Contains("Available tools", system);
        Assert.Contains("calc: Does maths", system);
        Assert.Contains("Available skills", system);
    }

    [Fact]
    public async Task BuildAsync_NoSystemSections_OnlyPromptAndTaskPresent()
    {
        var builder = BuilderWith(systemSections: []);
        var result = await builder.BuildAsync(State(), [], CancellationToken.None);

        Assert.DoesNotContain("Available tools", result.Messages[0].Content);
        Assert.DoesNotContain("Available skills", result.Messages[0].Content);
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
