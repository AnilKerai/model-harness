using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using Xunit;
using StateBudget = SapphireGuard.ModelHarness.Framework.State.Budget;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Smoke;

public sealed class SystemPromptAssemblyTests
{
    [Fact]
    public async Task AssembledSystemPrompt_KeepsUserPromptAndHarnessAndReActPriming()
    {
        // Regression for the SystemPromptGuide-clobber bug: WithSystemPrompt registers after the
        // default pipeline, so if it assigned the prompt (rather than prepending) it would wipe the
        // HarnessInstructionsGuide priming and the ReActGuide framing that ran first. The assembled
        // system message must carry all three, with the caller's prompt first.
        const string marker = "USER_PROMPT_MARKER";
        var services = new ServiceCollection();
        services.AddStandardModelHarness(b => b
            .WithSystemPrompt($"You are an agent. {marker}")
            .WithModel(_ => new FakeModelClient()));

        await using var provider = services.BuildServiceProvider();
        var builder = provider.GetRequiredService<IContextBuilder>();
        var state = AgentState.NewTask("do the thing", new StateBudget
        {
            MaxTurns = 5, MaxTotalTokens = 100_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromMinutes(1)
        }, DateTimeOffset.UtcNow);

        var system = (await builder.BuildAsync(state, [], CancellationToken.None)).Messages[0].Content;

        Assert.Contains(marker, system);                   // caller's prompt survives
        Assert.Contains("Harness observations", system);   // HarnessInstructionsGuide priming
        Assert.Contains("Reason and act", system);         // ReActGuide framing
        Assert.True(
            system.IndexOf(marker, StringComparison.Ordinal)
                < system.IndexOf("Harness observations", StringComparison.Ordinal),
            "the caller's prompt should come before the harness sections");
    }
}
