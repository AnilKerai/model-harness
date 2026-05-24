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
            .AddHarnessInstructionsGuideDefault()
            .AddTrajectoryGuideDefault()
            .AddMemoryGuideDefault()
            .AddToolSelectorGuideDefault()
            .AddToolCatalogueGuideDefault()
            .AddSkillsGuideDefault()
            .AddSensorRunnerDefault();

        var builder = new ModelHarnessBuilder(services);
        configure(builder);
        builder.ApplyTracers();
        builder.ApplyRateLimiters();
        return services;
    }

    // ── Internal wiring ──────────────────────────────────────────────────────

    private static IServiceCollection AddAgent(this IServiceCollection services)
    {
        services.TryAddSingleton<Agent>();
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
            new DefaultGuideRunner(sp.GetRequiredService<IEnumerable<IGuide>>()));
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

    private static IServiceCollection AddHarnessInstructionsGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, HarnessInstructionsGuide>();
        return services;
    }

    private static IServiceCollection AddTrajectoryGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, TrajectoryGuide>();
        return services;
    }

    private static IServiceCollection AddMemoryGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, MemoryGuide>();
        return services;
    }

    private static IServiceCollection AddToolSelectorGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, ToolSelectorGuide>();
        return services;
    }

    private static IServiceCollection AddToolCatalogueGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, ToolCatalogueGuide>();
        return services;
    }

    private static IServiceCollection AddSkillsGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, SkillsGuide>();
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
