using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SapphireGuard.ModelHarness.Framework;

/// <summary>
/// Composition helpers for wiring the harness into <see cref="IServiceCollection"/>.
///
/// Two patterns per abstraction:
/// <list type="bullet">
///   <item><c>AddXxx&lt;TImpl&gt;()</c> — explicit override; replaces any existing registration.</item>
///   <item><c>AddXxxDefault()</c> — registers the framework default via <c>TryAdd</c>,
///         so any explicit registration the consumer made wins regardless of call order.</item>
/// </list>
///
/// <see cref="AddModelHarness"/> is the aggregate. It registers the four built-in
/// guides (system prompt, trajectory, memory, tool selector), the guide runner,
/// the context builder, the budget enforcer, and the loop itself. Consumers
/// still need to register an <see cref="IModelClient"/>, an <see cref="IToolRegistry"/>,
/// an <see cref="ITracer"/>, and any <see cref="ITool"/> / <see cref="ISensor"/> /
/// custom <see cref="IGuide"/> instances.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the loop and all framework-provided defaults. Defaults use
    /// <c>TryAdd</c>, so any prior explicit registration is preserved.
    /// </summary>
    public static IServiceCollection AddModelHarness(this IServiceCollection services, string systemPrompt) =>
        services
            .AddHarnessLoop()
            .AddBudgetEnforcerDefault()
            .AddGuideRunnerDefault()
            .AddContextBuilderDefault()
            .AddSystemPromptGuide(systemPrompt)
            .AddTrajectoryGuideDefault()
            .AddMemoryGuideDefault()
            .AddToolSelectorGuideDefault()
            .AddSensorRunnerDefault();

    // ── Loop ────────────────────────────────────────────────────────────────

    public static IServiceCollection AddHarnessLoop(this IServiceCollection services)
    {
        services.TryAddSingleton<HarnessLoop>();
        return services;
    }

    // ── Infrastructure abstractions (no framework default) ──────────────────

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

    // ── Sensors ─────────────────────────────────────────────────────────────

    public static IServiceCollection AddSensorRunner<TImpl>(this IServiceCollection services)
        where TImpl : class, ISensorRunner =>
        services.Replace(ServiceDescriptor.Singleton<ISensorRunner, TImpl>());

    public static IServiceCollection AddSensorRunnerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ISensorRunner>(sp =>
            new DefaultSensorRunner(sp.GetRequiredService<IEnumerable<ISensor>>()));
        return services;
    }

    // ── Budget ───────────────────────────────────────────────────────────────

    public static IServiceCollection AddBudgetEnforcer<TImpl>(this IServiceCollection services)
        where TImpl : class, IBudgetEnforcer =>
        services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, TImpl>());

    public static IServiceCollection AddBudgetEnforcerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IBudgetEnforcer, DefaultBudgetEnforcer>();
        return services;
    }

    // ── Context builder ──────────────────────────────────────────────────────

    public static IServiceCollection AddContextBuilder<TImpl>(this IServiceCollection services)
        where TImpl : class, IContextBuilder =>
        services.Replace(ServiceDescriptor.Singleton<IContextBuilder, TImpl>());

    public static IServiceCollection AddContextBuilderDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IContextBuilder, DefaultContextBuilder>();
        return services;
    }

    // ── Guides ───────────────────────────────────────────────────────────────
    //
    // Guides form a collection, not a single registration, so the Add/Replace
    // duality used for single-instance abstractions does not apply here.
    // AddXxxGuideDefault() adds to the collection unconditionally; the opt-out
    // is simply not calling it. AddGuide<T>() is the open extension point for
    // consumer-defined guides, which run after the built-in ones.

    /// <summary>Registers a custom guide. Runs after the built-in guides in registration order.</summary>
    public static IServiceCollection AddGuide<TImpl>(this IServiceCollection services)
        where TImpl : class, IGuide
    {
        services.AddSingleton<IGuide, TImpl>();
        return services;
    }

    public static IServiceCollection AddGuideRunner<TImpl>(this IServiceCollection services)
        where TImpl : class, IGuideRunner =>
        services.Replace(ServiceDescriptor.Singleton<IGuideRunner, TImpl>());

    public static IServiceCollection AddGuideRunnerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IGuideRunner>(sp =>
            new DefaultGuideRunner(sp.GetRequiredService<IEnumerable<IGuide>>()));
        return services;
    }

    public static IServiceCollection AddSystemPromptGuide(this IServiceCollection services, string systemPrompt)
    {
        services.AddSingleton<IGuide>(_ => new SystemPromptGuide(systemPrompt));
        return services;
    }

    public static IServiceCollection AddTrajectoryGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, TrajectoryGuide>();
        return services;
    }

    public static IServiceCollection AddMemoryGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, MemoryGuide>();
        return services;
    }

    public static IServiceCollection AddToolSelectorGuideDefault(this IServiceCollection services)
    {
        services.AddSingleton<IGuide, ToolSelectorGuide>();
        return services;
    }
}
