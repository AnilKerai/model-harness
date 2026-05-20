using AgentHarness.Framework.Budget;
using AgentHarness.Framework.Context;
using AgentHarness.Framework.Loop;
using AgentHarness.Framework.Model;
using AgentHarness.Framework.Sensors;
using AgentHarness.Framework.Tools;
using AgentHarness.Framework.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentHarness.Framework.DependencyInjection;

/// <summary>
/// Composition helpers for wiring the harness into <see cref="IServiceCollection"/>.
///
/// Two patterns per abstraction:
/// <list type="bullet">
///   <item><c>AddXxx&lt;TImpl&gt;()</c> — explicit override; replaces any existing registration.</item>
///   <item><c>AddXxxDefault()</c> — registers the framework default via <c>TryAdd</c>,
///         so any explicit registration the consumer made wins regardless of call order.</item>
/// </list>
/// <see cref="AddAgentHarness"/> is an aggregate that registers every framework default
/// plus the loop itself. Consumers still need to register an <see cref="IModelClient"/>,
/// an <see cref="IToolRegistry"/>, an <see cref="ITracer"/>, and any <see cref="ITool"/>
/// / <see cref="ISensor"/> instances — those have no framework-provided defaults.
/// </summary>
public static class AgentHarnessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the harness loop and all framework-provided defaults. Defaults
    /// use <c>TryAdd</c>, so any prior explicit registration is preserved.
    /// </summary>
    public static IServiceCollection AddAgentHarness(this IServiceCollection services, string systemPrompt) =>
        services
            .AddHarnessLoop()
            .AddSensorRunnerDefault()
            .AddBudgetEnforcerDefault()
            .AddToolSelectorDefault()
            .AddTrajectoryCompactorDefault()
            .AddMemoryRetrieverDefault()
            .AddContextBuilderDefault(systemPrompt);

    public static IServiceCollection AddHarnessLoop(this IServiceCollection services)
    {
        services.TryAddSingleton<HarnessLoop>();
        return services;
    }

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

    public static IServiceCollection AddSensorRunner<TImpl>(this IServiceCollection services)
        where TImpl : class, ISensorRunner =>
        services.Replace(ServiceDescriptor.Singleton<ISensorRunner, TImpl>());

    public static IServiceCollection AddSensorRunnerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ISensorRunner>(sp =>
            new DefaultSensorRunner(sp.GetRequiredService<IEnumerable<ISensor>>()));
        return services;
    }

    public static IServiceCollection AddBudgetEnforcer<TImpl>(this IServiceCollection services)
        where TImpl : class, IBudgetEnforcer =>
        services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, TImpl>());

    public static IServiceCollection AddBudgetEnforcerDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IBudgetEnforcer, DefaultBudgetEnforcer>();
        return services;
    }

    public static IServiceCollection AddContextBuilder<TImpl>(this IServiceCollection services)
        where TImpl : class, IContextBuilder =>
        services.Replace(ServiceDescriptor.Singleton<IContextBuilder, TImpl>());

    public static IServiceCollection AddContextBuilderDefault(this IServiceCollection services, string systemPrompt)
    {
        services.TryAddSingleton<IContextBuilder>(sp => new DefaultContextBuilder(
            systemPrompt,
            sp.GetRequiredService<IToolSelector>(),
            sp.GetRequiredService<ITrajectoryCompactor>(),
            sp.GetRequiredService<IMemoryRetriever>()));
        return services;
    }

    public static IServiceCollection AddToolSelector<TImpl>(this IServiceCollection services)
        where TImpl : class, IToolSelector =>
        services.Replace(ServiceDescriptor.Singleton<IToolSelector, TImpl>());

    public static IServiceCollection AddToolSelectorDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IToolSelector, PassthroughToolSelector>();
        return services;
    }

    public static IServiceCollection AddTrajectoryCompactor<TImpl>(this IServiceCollection services)
        where TImpl : class, ITrajectoryCompactor =>
        services.Replace(ServiceDescriptor.Singleton<ITrajectoryCompactor, TImpl>());

    public static IServiceCollection AddTrajectoryCompactorDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrajectoryCompactor, NoopTrajectoryCompactor>();
        return services;
    }

    public static IServiceCollection AddMemoryRetriever<TImpl>(this IServiceCollection services)
        where TImpl : class, IMemoryRetriever =>
        services.Replace(ServiceDescriptor.Singleton<IMemoryRetriever, TImpl>());

    public static IServiceCollection AddMemoryRetrieverDefault(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryRetriever, NoopMemoryRetriever>();
        return services;
    }
}
