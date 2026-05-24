using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    // ── Resilient tool ───────────────────────────────────────────────────────

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> wrapped in a <see cref="ResilientTool"/>
    /// using the default Polly retry and circuit-breaker policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientTool<TImpl>(this ModelHarnessBuilder builder)
        where TImpl : class, ITool
    {
        builder.Services.AddSingleton<TImpl>();
        builder.Services.AddSingleton<ITool>(sp => new ResilientTool(sp.GetRequiredService<TImpl>()));
        return builder;
    }

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> wrapped in a <see cref="ResilientTool"/>
    /// using the provided <paramref name="pipeline"/> instead of the default policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientTool<TImpl>(
        this ModelHarnessBuilder builder,
        ResiliencePipeline<ToolResult> pipeline)
        where TImpl : class, ITool
    {
        builder.Services.AddSingleton<TImpl>();
        builder.Services.AddSingleton<ITool>(sp => new ResilientTool(sp.GetRequiredService<TImpl>(), pipeline));
        return builder;
    }

    // ── Resilient model client ───────────────────────────────────────────────

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> wrapped in a
    /// <see cref="ResilientModelClientDecorator"/> using the default Polly retry and
    /// circuit-breaker policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientModel<TImpl>(this ModelHarnessBuilder builder)
        where TImpl : class, IModelClient
    {
        builder.Services.TryAddSingleton<TImpl>();
        return builder.WithModel(sp => new ResilientModelClientDecorator(sp.GetRequiredService<TImpl>()));
    }

    /// <summary>
    /// Registers <typeparamref name="TImpl"/> wrapped in a
    /// <see cref="ResilientModelClientDecorator"/> using the provided
    /// <paramref name="pipeline"/> instead of the default policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientModel<TImpl>(
        this ModelHarnessBuilder builder,
        ResiliencePipeline<ModelResponse> pipeline)
        where TImpl : class, IModelClient
    {
        builder.Services.TryAddSingleton<TImpl>();
        return builder.WithModel(sp => new ResilientModelClientDecorator(sp.GetRequiredService<TImpl>(), pipeline));
    }

    /// <summary>
    /// Wraps the model client returned by <paramref name="factory"/> in a
    /// <see cref="ResilientModelClientDecorator"/> using the default Polly retry and
    /// circuit-breaker policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientModel(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IModelClient> factory) =>
        builder.WithModel(sp => new ResilientModelClientDecorator(factory(sp)));

    /// <summary>
    /// Wraps the model client returned by <paramref name="factory"/> in a
    /// <see cref="ResilientModelClientDecorator"/> using the provided
    /// <paramref name="pipeline"/> instead of the default policy.
    /// </summary>
    public static ModelHarnessBuilder WithResilientModel(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IModelClient> factory,
        ResiliencePipeline<ModelResponse> pipeline) =>
        builder.WithModel(sp => new ResilientModelClientDecorator(factory(sp), pipeline));
}
