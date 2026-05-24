using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Memory;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Skills;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework;

/// <summary>
/// Fluent builder for configuring the model harness. Obtained via the
/// <see cref="DependencyInjection.AddModelHarness"/> callback; not constructed directly.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ModelHarnessBuilder(IServiceCollection services)
{
    private readonly List<Func<IServiceProvider, ITracer>> _tracers = [];

    /// <summary>
    /// Direct access to the underlying <see cref="IServiceCollection"/> for registrations
    /// not covered by the builder methods.
    /// </summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>Sets the agent's system prompt.</summary>
    public ModelHarnessBuilder WithSystemPrompt(string systemPrompt)
    {
        Services.AddSingleton<IGuide>(_ => new SystemPromptGuide(systemPrompt));
        return this;
    }

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> as the model client by type.
    /// Use <see cref="WithModel(Func{IServiceProvider, IModelClient})"/> when you need
    /// to pass constructor options.
    /// </summary>
    public ModelHarnessBuilder WithModel<TImpl>() where TImpl : class, IModelClient
    {
        Services.Replace(ServiceDescriptor.Singleton<IModelClient, TImpl>());
        return this;
    }

    /// <summary>Registers a model client via factory. Replaces any previously registered model client.</summary>
    public ModelHarnessBuilder WithModel(Func<IServiceProvider, IModelClient> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>
    /// Adds a tracer by type. Multiple calls are accumulated and automatically composed
    /// into a <see cref="CompositeTracer"/> at resolution time.
    /// </summary>
    public ModelHarnessBuilder WithTracer<TImpl>() where TImpl : class, ITracer
    {
        Services.TryAddSingleton<TImpl>();
        _tracers.Add(sp => sp.GetRequiredService<TImpl>());
        return this;
    }

    /// <summary>
    /// Adds a tracer via factory. Multiple calls are accumulated and automatically
    /// composed into a <see cref="CompositeTracer"/> at resolution time.
    /// </summary>
    public ModelHarnessBuilder WithTracer(Func<IServiceProvider, ITracer> factory)
    {
        _tracers.Add(factory);
        return this;
    }

    /// <summary>Registers a tool registry by type.</summary>
    public ModelHarnessBuilder WithToolRegistry<TImpl>() where TImpl : class, IToolRegistry
    {
        Services.Replace(ServiceDescriptor.Singleton<IToolRegistry, TImpl>());
        return this;
    }

    /// <summary>Registers a tool registry via factory.</summary>
    public ModelHarnessBuilder WithToolRegistry(Func<IServiceProvider, IToolRegistry> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Replaces the default <see cref="IBudgetEnforcer"/> with a custom implementation.</summary>
    public ModelHarnessBuilder WithBudgetEnforcer<TImpl>() where TImpl : class, IBudgetEnforcer
    {
        Services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, TImpl>());
        return this;
    }

    /// <summary>Replaces the default <see cref="IContextBuilder"/> with a custom implementation.</summary>
    public ModelHarnessBuilder WithContextBuilder<TImpl>() where TImpl : class, IContextBuilder
    {
        Services.Replace(ServiceDescriptor.Singleton<IContextBuilder, TImpl>());
        return this;
    }

    /// <summary>Replaces the default <see cref="ISensorRunner"/> with a custom implementation.</summary>
    public ModelHarnessBuilder WithSensorRunner<TImpl>() where TImpl : class, ISensorRunner
    {
        Services.Replace(ServiceDescriptor.Singleton<ISensorRunner, TImpl>());
        return this;
    }

    /// <summary>Replaces the default <see cref="IGuideRunner"/> with a custom implementation.</summary>
    public ModelHarnessBuilder WithGuideRunner<TImpl>() where TImpl : class, IGuideRunner
    {
        Services.Replace(ServiceDescriptor.Singleton<IGuideRunner, TImpl>());
        return this;
    }

    /// <summary>Registers a checkpoint store by type.</summary>
    public ModelHarnessBuilder WithCheckpointStore<TImpl>() where TImpl : class, ICheckpointStore
    {
        Services.Replace(ServiceDescriptor.Singleton<ICheckpointStore, TImpl>());
        return this;
    }

    /// <summary>Registers a checkpoint store via factory.</summary>
    public ModelHarnessBuilder WithCheckpointStore(Func<IServiceProvider, ICheckpointStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Registers a memory store by type.</summary>
    public ModelHarnessBuilder WithMemoryStore<TImpl>() where TImpl : class, IMemoryStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IMemoryStore, TImpl>());
        return this;
    }

    /// <summary>Registers a memory store via factory.</summary>
    public ModelHarnessBuilder WithMemoryStore(Func<IServiceProvider, IMemoryStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Registers a tool selector by type.</summary>
    public ModelHarnessBuilder WithToolSelector<TImpl>() where TImpl : class, IToolSelector
    {
        Services.Replace(ServiceDescriptor.Singleton<IToolSelector, TImpl>());
        return this;
    }

    /// <summary>Registers a tool selector via factory.</summary>
    public ModelHarnessBuilder WithToolSelector(Func<IServiceProvider, IToolSelector> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Registers a skill store by type.</summary>
    public ModelHarnessBuilder WithSkillStore<TImpl>() where TImpl : class, ISkillStore
    {
        Services.Replace(ServiceDescriptor.Singleton<ISkillStore, TImpl>());
        return this;
    }

    /// <summary>Registers a skill store via factory.</summary>
    public ModelHarnessBuilder WithSkillStore(Func<IServiceProvider, ISkillStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Registers a tool by type. Multiple calls are additive.</summary>
    public ModelHarnessBuilder WithTool<TImpl>() where TImpl : class, ITool
    {
        Services.AddSingleton<ITool, TImpl>();
        return this;
    }

    /// <summary>Registers a tool via factory. Multiple calls are additive.</summary>
    public ModelHarnessBuilder WithTool(Func<IServiceProvider, ITool> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Registers a sensor by type. Multiple calls are additive. Sensors run in parallel
    /// at each declared <see cref="HookPoint"/>.
    /// </summary>
    public ModelHarnessBuilder WithSensor<TImpl>() where TImpl : class, ISensor
    {
        Services.AddSingleton<ISensor, TImpl>();
        return this;
    }

    /// <summary>
    /// Registers a sensor via factory. Multiple calls are additive. Sensors run in
    /// parallel at each declared <see cref="HookPoint"/>.
    /// </summary>
    public ModelHarnessBuilder WithSensor(Func<IServiceProvider, ISensor> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Registers a custom guide by type. Multiple calls are additive. Custom guides run
    /// after the seven built-in guides in registration order.
    /// </summary>
    public ModelHarnessBuilder WithGuide<TImpl>() where TImpl : class, IGuide
    {
        Services.AddSingleton<IGuide, TImpl>();
        return this;
    }

    internal void ApplyTracers()
    {
        if (_tracers.Count == 0) return;

        Services.Replace(ServiceDescriptor.Singleton<ITracer>(sp => _tracers.Count == 1
            ? _tracers[0](sp)
            : new CompositeTracer(_tracers.Select(f => f(sp)).ToArray())));
    }
}
