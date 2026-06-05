using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Security;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Sensors;

public sealed class TaintTrackingSensorTests
{
    private static readonly TrustPolicy Policy = new(
        untrustedSources: ["fetch_webpage", "read_document"],
        privilegedActions: ["send_email", "execute_code"]);

    private static readonly TaintTrackingSensor Sut = new(Policy);

    private static AgentState EmptyState() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxContextTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    });

    private static ToolCallStep CompletedStep(string toolName, string content = "some content", bool isError = false) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), toolName, JsonDocument.Parse("{}").RootElement),
            new ToolResult("id", content, IsError: isError));

    private static ToolCallStep PendingStep(string toolName) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            new ToolCall(Guid.NewGuid().ToString("n"), toolName, JsonDocument.Parse("{}").RootElement),
            new ToolResult("id", "(pending)"));

    // ── PostToolCall ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PostToolCall_UntrustedSource_Intervenes()
    {
        var step = CompletedStep("fetch_webpage");
        var result = await Sut.CheckAsync(HookPoint.PostToolCall, EmptyState(), step, CancellationToken.None);
        Assert.True(result.IsIntervene);
        Assert.Contains("fetch_webpage", result.Reason);
    }

    [Fact]
    public async Task PostToolCall_TrustedTool_Passes()
    {
        var step = CompletedStep("calculator");
        var result = await Sut.CheckAsync(HookPoint.PostToolCall, EmptyState(), step, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PostToolCall_UntrustedSourceWithError_Passes()
    {
        var step = CompletedStep("fetch_webpage", isError: true);
        var result = await Sut.CheckAsync(HookPoint.PostToolCall, EmptyState(), step, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PostToolCall_NonToolCallStep_Passes()
    {
        var modelStep = new ModelCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [], Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m },
            Usage: Usage.Zero, Cost: 0m);
        var result = await Sut.CheckAsync(HookPoint.PostToolCall, EmptyState(), modelStep, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    // ── PreToolCall ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PreToolCall_PrivilegedAction_WithTaintedTrajectory_Blocks()
    {
        var taintedStep = CompletedStep("fetch_webpage");
        var state = EmptyState().AppendStep(taintedStep);
        var preStep = PendingStep("send_email");

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, preStep, CancellationToken.None);

        Assert.True(result.IsIntervene);
        Assert.Contains("send_email", result.Reason);
        Assert.Contains("fetch_webpage", result.Reason);
    }

    [Fact]
    public async Task PreToolCall_PrivilegedAction_WithCleanTrajectory_Passes()
    {
        var trustedStep = CompletedStep("calculator");
        var state = EmptyState().AppendStep(trustedStep);
        var preStep = PendingStep("send_email");

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, preStep, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PreToolCall_NonPrivilegedAction_WithTaintedTrajectory_Passes()
    {
        var taintedStep = CompletedStep("fetch_webpage");
        var state = EmptyState().AppendStep(taintedStep);
        var preStep = PendingStep("calculator");

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, preStep, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PreToolCall_PrivilegedAction_WithOnlyFailedUntrustedStep_Passes()
    {
        var failedTaint = CompletedStep("fetch_webpage", isError: true);
        var state = EmptyState().AppendStep(failedTaint);
        var preStep = PendingStep("send_email");

        var result = await Sut.CheckAsync(HookPoint.PreToolCall, state, preStep, CancellationToken.None);

        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PreToolCall_EmptyTrajectory_Passes()
    {
        var preStep = PendingStep("send_email");
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, EmptyState(), preStep, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    [Fact]
    public async Task PreToolCall_NonToolCallStep_Passes()
    {
        var modelStep = new ModelCallStep(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Prompt: [], Response: new ModelResponse { Text = "ok", ToolCalls = [], StopReason = StopReason.EndTurn, Usage = Usage.Zero, Cost = 0m },
            Usage: Usage.Zero, Cost: 0m);
        var result = await Sut.CheckAsync(HookPoint.PreToolCall, EmptyState(), modelStep, CancellationToken.None);
        Assert.False(result.IsIntervene);
    }

    // ── TrustPolicy ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fetch_webpage", true)]
    [InlineData("FETCH_WEBPAGE", true)]
    [InlineData("Fetch_Webpage", true)]
    [InlineData("calculator", false)]
    public void TrustPolicy_IsUntrustedSource_IsCaseInsensitive(string toolName, bool expected)
    {
        Assert.Equal(expected, Policy.IsUntrustedSource(toolName));
    }

    [Theory]
    [InlineData("send_email", true)]
    [InlineData("SEND_EMAIL", true)]
    [InlineData("calculator", false)]
    public void TrustPolicy_IsPrivilegedAction_IsCaseInsensitive(string toolName, bool expected)
    {
        Assert.Equal(expected, Policy.IsPrivilegedAction(toolName));
    }
}
