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
    // ── Builder extensions: resilient tool ──────────────────────────────────

    public static ModelHarnessBuilder WithResilientTool<TImpl>(this ModelHarnessBuilder builder)
        where TImpl : class, ITool
    {
        builder.Services.AddSingleton<TImpl>();
        builder.Services.AddSingleton<ITool>(sp => new ResilientTool(sp.GetRequiredService<TImpl>()));
        return builder;
    }

    public static ModelHarnessBuilder WithResilientTool<TImpl>(
        this ModelHarnessBuilder builder,
        ResiliencePipeline<ToolResult> pipeline)
        where TImpl : class, ITool
    {
        builder.Services.AddSingleton<TImpl>();
        builder.Services.AddSingleton<ITool>(sp => new ResilientTool(sp.GetRequiredService<TImpl>(), pipeline));
        return builder;
    }

    // ── Builder extensions: resilient model client ───────────────────────────

    public static ModelHarnessBuilder WithResilientModel<TImpl>(this ModelHarnessBuilder builder)
        where TImpl : class, IModelClient
    {
        builder.Services.TryAddSingleton<TImpl>();
        return builder.WithModel(sp => new ResilientModelClientDecorator(sp.GetRequiredService<TImpl>()));
    }

    public static ModelHarnessBuilder WithResilientModel<TImpl>(
        this ModelHarnessBuilder builder,
        ResiliencePipeline<ModelResponse> pipeline)
        where TImpl : class, IModelClient
    {
        builder.Services.TryAddSingleton<TImpl>();
        return builder.WithModel(sp => new ResilientModelClientDecorator(sp.GetRequiredService<TImpl>(), pipeline));
    }

    public static ModelHarnessBuilder WithResilientModel(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IModelClient> factory) =>
        builder.WithModel(sp => new ResilientModelClientDecorator(factory(sp)));

    public static ModelHarnessBuilder WithResilientModel(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IModelClient> factory,
        ResiliencePipeline<ModelResponse> pipeline) =>
        builder.WithModel(sp => new ResilientModelClientDecorator(factory(sp), pipeline));
}
