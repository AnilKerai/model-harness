using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
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
    public static IServiceCollection AddModelHarness(this IServiceCollection services, string systemPrompt) =>
        services
            .AddAgent()
            .AddHarnessLoop()
            .AddBudgetEnforcerDefault()
            .AddGuideRunnerDefault()
            .AddContextBuilderDefault()
            .AddMemoryStoreDefault()
            .AddSkillStoreDefault()
            .AddToolSelectorDefault()
            .AddCheckpointStoreDefault()
            .AddSystemPromptGuide(systemPrompt)
            .AddHarnessInstructionsGuideDefault()
            .AddTrajectoryGuideDefault()
            .AddMemoryGuideDefault()
            .AddToolSelectorGuideDefault()
            .AddToolCatalogueGuideDefault()
            .AddSkillsGuideDefault()
            .AddSensorRunnerDefault();

    // ── Public API ───────────────────────────────────────────────────────────

    public static IServiceCollection AddModelClient<TImpl>(this IServiceCollection services)
        where TImpl : class, IModelClient =>
        services.Replace(ServiceDescriptor.Singleton<IModelClient, TImpl>());

    public static IServiceCollection AddModelClient(this IServiceCollection services, Func<IServiceProvider, IModelClient> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddToolRegistry<TImpl>(this IServiceCollection services)
        where TImpl : class, IToolRegistry =>
        services.Replace(ServiceDescriptor.Singleton<IToolRegistry, TImpl>());

    public static IServiceCollection AddToolRegistry(this IServiceCollection services, Func<IServiceProvider, IToolRegistry> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddTracer<TImpl>(this IServiceCollection services)
        where TImpl : class, ITracer =>
        services.Replace(ServiceDescriptor.Singleton<ITracer, TImpl>());

    public static IServiceCollection AddTracer(this IServiceCollection services, Func<IServiceProvider, ITracer> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddBudgetEnforcer<TImpl>(this IServiceCollection services)
        where TImpl : class, IBudgetEnforcer =>
        services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, TImpl>());

    public static IServiceCollection AddContextBuilder<TImpl>(this IServiceCollection services)
        where TImpl : class, IContextBuilder =>
        services.Replace(ServiceDescriptor.Singleton<IContextBuilder, TImpl>());

    public static IServiceCollection AddSensorRunner<TImpl>(this IServiceCollection services)
        where TImpl : class, ISensorRunner =>
        services.Replace(ServiceDescriptor.Singleton<ISensorRunner, TImpl>());

    public static IServiceCollection AddGuideRunner<TImpl>(this IServiceCollection services)
        where TImpl : class, IGuideRunner =>
        services.Replace(ServiceDescriptor.Singleton<IGuideRunner, TImpl>());

    public static IServiceCollection AddGuide<TImpl>(this IServiceCollection services)
        where TImpl : class, IGuide
    {
        services.AddSingleton<IGuide, TImpl>();
        return services;
    }



    public static IServiceCollection AddCheckpointStore<TImpl>(this IServiceCollection services)
        where TImpl : class, ICheckpointStore =>
        services.Replace(ServiceDescriptor.Singleton<ICheckpointStore, TImpl>());

    public static IServiceCollection AddCheckpointStore(this IServiceCollection services, Func<IServiceProvider, ICheckpointStore> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddMemoryStore<TImpl>(this IServiceCollection services)
        where TImpl : class, IMemoryStore =>
        services.Replace(ServiceDescriptor.Singleton<IMemoryStore, TImpl>());

    public static IServiceCollection AddMemoryStore(this IServiceCollection services, Func<IServiceProvider, IMemoryStore> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddToolSelector<TImpl>(this IServiceCollection services)
        where TImpl : class, IToolSelector =>
        services.Replace(ServiceDescriptor.Singleton<IToolSelector, TImpl>());

    public static IServiceCollection AddToolSelector(this IServiceCollection services, Func<IServiceProvider, IToolSelector> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

    public static IServiceCollection AddSkillStore<TImpl>(this IServiceCollection services)
        where TImpl : class, ISkillStore =>
        services.Replace(ServiceDescriptor.Singleton<ISkillStore, TImpl>());

    public static IServiceCollection AddSkillStore(this IServiceCollection services, Func<IServiceProvider, ISkillStore> factory) =>
        services.Replace(ServiceDescriptor.Singleton(factory));

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

    private static IServiceCollection AddSystemPromptGuide(this IServiceCollection services, string systemPrompt)
    {
        services.AddSingleton<IGuide>(_ => new SystemPromptGuide(systemPrompt));
        return services;
    }

    private static IServiceCollection AddHarnessInstructionsGuideDefault(this IServiceCollection services) =>
        services.AddGuide<HarnessInstructionsGuide>();

    private static IServiceCollection AddTrajectoryGuideDefault(this IServiceCollection services) =>
        services.AddGuide<TrajectoryGuide>();

    private static IServiceCollection AddMemoryGuideDefault(this IServiceCollection services) =>
        services.AddGuide<MemoryGuide>();

    private static IServiceCollection AddToolSelectorGuideDefault(this IServiceCollection services) =>
        services.AddGuide<ToolSelectorGuide>();

    private static IServiceCollection AddToolCatalogueGuideDefault(this IServiceCollection services) =>
        services.AddGuide<ToolCatalogueGuide>();

    private static IServiceCollection AddSkillsGuideDefault(this IServiceCollection services) =>
        services.AddGuide<SkillsGuide>();
}
