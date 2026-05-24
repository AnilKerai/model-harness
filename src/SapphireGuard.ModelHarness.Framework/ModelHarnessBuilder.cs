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

[ExcludeFromCodeCoverage]
public sealed class ModelHarnessBuilder(IServiceCollection services)
{
    private readonly List<Func<IServiceProvider, ITracer>> _tracers = [];

    public IServiceCollection Services { get; } = services;

    public ModelHarnessBuilder WithSystemPrompt(string systemPrompt)
    {
        Services.AddSingleton<IGuide>(_ => new SystemPromptGuide(systemPrompt));
        return this;
    }

    public ModelHarnessBuilder WithModel<TImpl>() where TImpl : class, IModelClient
    {
        Services.Replace(ServiceDescriptor.Singleton<IModelClient, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithModel(Func<IServiceProvider, IModelClient> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithTracer<TImpl>() where TImpl : class, ITracer
    {
        Services.TryAddSingleton<TImpl>();
        _tracers.Add(sp => sp.GetRequiredService<TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithTracer(Func<IServiceProvider, ITracer> factory)
    {
        _tracers.Add(factory);
        return this;
    }

    public ModelHarnessBuilder WithToolRegistry<TImpl>() where TImpl : class, IToolRegistry
    {
        Services.Replace(ServiceDescriptor.Singleton<IToolRegistry, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithToolRegistry(Func<IServiceProvider, IToolRegistry> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithBudgetEnforcer<TImpl>() where TImpl : class, IBudgetEnforcer
    {
        Services.Replace(ServiceDescriptor.Singleton<IBudgetEnforcer, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithContextBuilder<TImpl>() where TImpl : class, IContextBuilder
    {
        Services.Replace(ServiceDescriptor.Singleton<IContextBuilder, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithSensorRunner<TImpl>() where TImpl : class, ISensorRunner
    {
        Services.Replace(ServiceDescriptor.Singleton<ISensorRunner, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithGuideRunner<TImpl>() where TImpl : class, IGuideRunner
    {
        Services.Replace(ServiceDescriptor.Singleton<IGuideRunner, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithCheckpointStore<TImpl>() where TImpl : class, ICheckpointStore
    {
        Services.Replace(ServiceDescriptor.Singleton<ICheckpointStore, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithCheckpointStore(Func<IServiceProvider, ICheckpointStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithMemoryStore<TImpl>() where TImpl : class, IMemoryStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IMemoryStore, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithMemoryStore(Func<IServiceProvider, IMemoryStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithToolSelector<TImpl>() where TImpl : class, IToolSelector
    {
        Services.Replace(ServiceDescriptor.Singleton<IToolSelector, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithToolSelector(Func<IServiceProvider, IToolSelector> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithSkillStore<TImpl>() where TImpl : class, ISkillStore
    {
        Services.Replace(ServiceDescriptor.Singleton<ISkillStore, TImpl>());
        return this;
    }

    public ModelHarnessBuilder WithSkillStore(Func<IServiceProvider, ISkillStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    public ModelHarnessBuilder WithTool<TImpl>() where TImpl : class, ITool
    {
        Services.AddSingleton<ITool, TImpl>();
        return this;
    }

    public ModelHarnessBuilder WithTool(Func<IServiceProvider, ITool> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }

    public ModelHarnessBuilder WithSensor<TImpl>() where TImpl : class, ISensor
    {
        Services.AddSingleton<ISensor, TImpl>();
        return this;
    }

    public ModelHarnessBuilder WithSensor(Func<IServiceProvider, ISensor> factory)
    {
        Services.AddSingleton(factory);
        return this;
    }

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
