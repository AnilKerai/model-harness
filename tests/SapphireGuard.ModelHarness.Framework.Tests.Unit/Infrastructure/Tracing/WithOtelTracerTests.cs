using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tracing;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Tracing;

public sealed class WithOtelTracerTests
{
    private static readonly Framework.State.Budget TwoTurns = new()
    {
        MaxTurns = 2, MaxTotalTokens = 100_000, MaxCost = 1m, MaxWallClock = TimeSpan.FromSeconds(10)
    };

    // Runs one task through the harness and returns the invoke_agent roots it produced.
    private static async Task<List<Activity>> RootsOfOneRun(Action<IServiceCollection> register)
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == OpenTelemetryTracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (captured) captured.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        register(services);
        await using var provider = services.BuildServiceProvider();
        var outcome = await provider.GetRequiredService<Agent>().RunAsync("What is 12 * 7?", budget: TwoTurns);

        lock (captured)
            return captured
                .Where(a => a.OperationName == "invoke_agent" && (string?)a.GetTagItem("harness.task.id") == outcome.TaskId)
                .ToList();
    }

    [Fact]
    public async Task On_the_standard_harness_it_reconfigures_the_default_tracer_instead_of_adding_a_second()
    {
        // The standard harness already adds an OpenTelemetryTracer. A second one doubled every span — and every
        // token and cost figure a backend sums from them.
        var roots = await RootsOfOneRun(services => services.AddStandardModelHarness(b => b
            .WithOtelTracer(enableSensitiveData: true, agentName: "solo")
            .WithTool<CalculatorTool>()
            .WithModel(_ => new FakeModelClient())));

        var root = Assert.Single(roots);
        Assert.Equal("solo", root.GetTagItem("gen_ai.agent.name"));
        Assert.NotNull(root.GetTagItem("harness.task.text")); // the content-capture setting took effect too
    }

    [Fact]
    public async Task On_the_standard_chat_harness_it_reconfigures_the_default_tracer_instead_of_adding_a_second()
    {
        var roots = await RootsOfOneRun(services => services.AddStandardChatHarness(b => b
            .WithOtelTracer(agentName: "chat")
            .WithTool<CalculatorTool>()
            .WithModel(_ => new FakeModelClient())));

        Assert.Equal("chat", Assert.Single(roots).GetTagItem("gen_ai.agent.name"));
    }

    [Fact]
    public void It_composes_with_other_tracers_rather_than_replacing_them()
    {
        var services = new ServiceCollection();
        services.AddStandardModelHarness(b => b
            .WithConsoleTracer()
            .WithOtelTracer(agentName: "x")
            .WithModel(_ => new FakeModelClient()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<CompositeTracer>(provider.GetRequiredService<ITracer>());
    }
}
