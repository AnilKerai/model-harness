using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Persistence;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Smoke;

public sealed class DiSmokeTests
{
    private static readonly Framework.State.Budget OneTurn = new()
    {
        MaxTurns = 1,
        MaxTotalTokens = 100_000,
        MaxCost = 1m,
        MaxWallClock = TimeSpan.FromSeconds(10)
    };

    [Fact]
    public async Task AddStandardModelHarness_ResolvesAgentAndCompletesOneTurn()
    {
        var services = new ServiceCollection();
        services.AddStandardModelHarness(builder => builder
            .WithSystemPrompt("You are a test agent.")
            .WithTool<CalculatorTool>()
            .WithModel(_ => new FakeModelClient()));

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: OneTurn);

        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task AddStandardChatHarness_ResolvesAgentAndCompletesOneTurn()
    {
        var services = new ServiceCollection();
        services.AddStandardChatHarness(builder => builder
            .WithSystemPrompt("You are a test chat agent.")
            .WithTool<CalculatorTool>()
            .WithModel(_ => new FakeModelClient()));

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: OneTurn);

        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task AddModelHarness_WithPiiRedactionSensor_ResolvesAgentAndCompletesOneTurn()
    {
        var services = new ServiceCollection();
        services.AddModelHarness(builder => builder
            .WithSystemPrompt("You are a test agent.")
            .WithToolRegistry<InMemoryToolRegistry>()
            .WithTool<CalculatorTool>()
            .WithSensor<PiiRedactionSensor>()
            .WithSensor<StuckDetector>()
            .WithModel(_ => new FakeModelClient()));

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: OneTurn);

        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task AddModelHarness_WithToolResultSanityCheckSensor_ResolvesAgentAndCompletesOneTurn()
    {
        var services = new ServiceCollection();
        services.AddModelHarness(builder => builder
            .WithSystemPrompt("You are a test agent.")
            .WithToolRegistry<InMemoryToolRegistry>()
            .WithTool<CalculatorTool>()
            .WithSensor<StuckDetector>()
            .WithSensor(_ => new ToolResultSanityCheckSensor(toolValidators: new Dictionary<string, Func<string, string?>>()))
            .WithModel(_ => new FakeModelClient()));

        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: OneTurn);

        Assert.NotNull(outcome);
    }

    [Fact]
    public async Task AddModelHarness_WithFileCheckpointStore_ResolvesAgentAndCompletesOneTurn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smoke-checkpoints-" + Guid.NewGuid().ToString("n"));
        try
        {
            var services = new ServiceCollection();
            services.AddModelHarness(builder => builder
                .WithSystemPrompt("You are a test agent.")
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithTool<CalculatorTool>()
                .WithFileCheckpointStore(dir)
                .WithModel(_ => new FakeModelClient()));

            await using var provider = services.BuildServiceProvider();
            var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: OneTurn);

            Assert.NotNull(outcome);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddModelHarness_WithLearning_ResolvesAgentAndCompletesOneTurn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smoke-skills-" + Guid.NewGuid().ToString("n"));
        try
        {
            var services = new ServiceCollection();
            services.AddModelHarness(builder => builder
                .WithSystemPrompt("You are a test agent.")
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithLearning(dir)
                .WithModel(_ => new FakeModelClient()));

            await using var provider = services.BuildServiceProvider();
            var outcome = await provider.GetRequiredService<Agent>().RunAsync("Greet the user.", budget: OneTurn);

            Assert.NotNull(outcome);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddModelHarness_WithSkillsAndLearning_ResolvesAgentAndCompletesOneTurn()
    {
        var agentDir = Path.Combine(Path.GetTempPath(), "smoke-agent-skills-" + Guid.NewGuid().ToString("n"));
        var userDir = Path.Combine(Path.GetTempPath(), "smoke-user-skills-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(userDir);

            var services = new ServiceCollection();
            services.AddModelHarness(builder => builder
                .WithSystemPrompt("You are a test agent.")
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithLearning(agentDir)
                .WithSkills(userDir)
                .WithModel(_ => new FakeModelClient()));

            await using var provider = services.BuildServiceProvider();
            var outcome = await provider.GetRequiredService<Agent>().RunAsync("Greet the user.", budget: OneTurn);

            Assert.NotNull(outcome);
        }
        finally
        {
            if (Directory.Exists(agentDir)) Directory.Delete(agentDir, recursive: true);
            if (Directory.Exists(userDir)) Directory.Delete(userDir, recursive: true);
        }
    }
}
