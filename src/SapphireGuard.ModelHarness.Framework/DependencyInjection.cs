using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SapphireGuard.ModelHarness.Framework;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>
    /// Registers the model harness with all framework defaults. Use the <paramref name="configure"/>
    /// callback to wire the model, tools, sensors, tracers, and any other seams.
    /// </summary>
    public static IServiceCollection AddModelHarness(this IServiceCollection services, Action<ModelHarnessBuilder> configure)
    {
        services
            .AddAgent()
            .AddTimeProviderDefault()
            .AddHarnessLoop()
            .AddBudgetEnforcerDefault()
            .AddRateLimiterDefault()
            .AddGuideRunnerDefault()
            .AddContextBuilderDefault()
            .AddMemoryStoreDefault()
            .AddSkillStoreDefault()
            .AddToolSelectorDefault()
            .AddCheckpointStoreDefault()
            .AddTracerDefault()
            .AddToolRegistryDefault()
            .AddCompactionStrategyDefault()
            .AddTrajectoryGuideDefault()
            .AddHumanNotifierDefault()
            .AddDefaultGuidePipeline()
            .AddSensorRunnerDefault();

        var builder = new ModelHarnessBuilder(services);
        configure(builder);
        builder.ApplyTracers();
        builder.ApplyRateLimiters();
        builder.ApplyGuides();
        return services;
    }

    /// <summary>
    /// Registers the harness configured for multi-turn chat: a per-turn budget
    /// (<see cref="TurnScopedBudgetEnforcer"/>) so each user turn gets a fresh allowance, and a
    /// trajectory guide that does not pin the first message as a fixed goal. Task-completion
    /// sensors are deliberately not wired — chat has no single goal to make progress toward; add
    /// any you want via <paramref name="configure"/>. Continue a conversation across turns with
    /// <see cref="State.AgentState.WithUserMessage"/>. The caller still supplies the model.
    /// </summary>
    public static IServiceCollection AddChatHarness(this IServiceCollection services, Action<ModelHarnessBuilder> configure) =>
        services.AddModelHarness(builder =>
        {
            builder
                .WithBudgetEnforcer<TurnScopedBudgetEnforcer>()
                .WithTrajectoryGuide(sp => new HeadEvictionTrajectoryGuide(
                    sp.GetRequiredService<ICompactionStrategy>(), pinOriginalGoal: false));
            configure(builder);
        });

    // ── Internal wiring ──────────────────────────────────────────────────────

    private static IServiceCollection AddAgent(this IServiceCollection services)
    {
        services.TryAddSingleton<Agent>();
        return services;
    }

    private static IServiceCollection AddTimeProviderDefault(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    private static IServiceCollection AddHarnessLoop(this IServiceCollection services)
    {
        services.TryAddSingleton<HarnessLoop>();
        return services;
    }

    private static IServiceCollection AddBudgetEnforcerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IBudgetEnforcer, DefaultBudgetEnforcer>();
        return services;
    }

    private static IServiceCollection AddRateLimiterDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IRateLimiter, NullRateLimiter>();
        return services;
    }

    private static IServiceCollection AddContextBuilderDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IContextBuilder, DefaultContextBuilder>();
        return services;
    }

    private static IServiceCollection AddGuideRunnerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IGuideRunner>(sp =>
            new DefaultGuideRunner(
                sp.GetRequiredService<IEnumerable<IGuide>>(),
                sp.GetRequiredService<ITrajectoryGuide>()));
        return services;
    }

    private static IServiceCollection AddSensorRunnerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ISensorRunner>(sp =>
            new DefaultSensorRunner(sp.GetRequiredService<IEnumerable<ISensor>>()));
        return services;
    }

    private static IServiceCollection AddCheckpointStoreDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ICheckpointStore, NullCheckpointStore>();
        return services;
    }

    private static IServiceCollection AddMemoryStoreDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryStore, NullMemoryStore>();
        return services;
    }

    private static IServiceCollection AddToolSelectorDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolSelector, PassthroughToolSelector>();
        return services;
    }

    private static IServiceCollection AddSkillStoreDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ISkillStore, NullSkillStore>();
        return services;
    }

    private static IServiceCollection AddCompactionStrategyDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ICompactionStrategy, NullCompactionStrategy>();
        return services;
    }

    private static IServiceCollection AddTrajectoryGuideDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrajectoryGuide>(sp =>
            new HeadEvictionTrajectoryGuide(sp.GetRequiredService<ICompactionStrategy>()));
        return services;
    }

    // Built-in guides in explicit execution order. The ITrajectoryGuide (HeadEvictionTrajectoryGuide
    // by default) is absent — DefaultGuideRunner resolves it separately and always runs it last.
    // Custom guides registered via builder.WithGuide() are inserted between these and the trajectory guide.
    private static IServiceCollection AddDefaultGuidePipeline(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, HarnessInstructionsGuide>();
        services.AddSingleton<IGuide, ReActGuide>();
        services.AddSingleton<IGuide, MemoryGuide>();
        // ToolSelectorGuide must precede ToolCatalogueGuide — the catalogue renders
        // whatever tools the selector has approved for this turn.
        services.AddSingleton<IGuide, ToolSelectorGuide>();
        services.AddSingleton<IGuide, ToolCatalogueGuide>();
        services.AddSingleton<IGuide, SkillsGuide>();
        return services;
    }

    private static IServiceCollection AddHumanNotifierDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IHumanNotifier, NullHumanNotifier>();
        return services;
    }

    private static IServiceCollection AddTracerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ITracer, NullTracer>();
        return services;
    }

    private static IServiceCollection AddToolRegistryDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolRegistry, NullToolRegistry>();
        return services;
    }
}
