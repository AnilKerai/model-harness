using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static IServiceCollection AddFileSkillStore(this IServiceCollection services, string directory) =>
        services.AddSkillStore(_ => new FileSkillStore(directory));

    public static IServiceCollection AddSkillTools(this IServiceCollection services)
    {
        services.AddSingleton<ITool, SkillManageTool>();
        services.AddSingleton<ITool, SkillViewTool>();
        return services;
    }

    public static IServiceCollection AddAskHumanTool<TChannel>(this IServiceCollection services)
        where TChannel : class, IHumanChannel
    {
        services.AddSingleton<IHumanChannel, TChannel>();
        services.AddSingleton<ITool, AskHumanTool>();
        return services;
    }

    public static IServiceCollection AddAskHumanTool(
        this IServiceCollection services,
        Func<IServiceProvider, IHumanChannel> factory)
    {
        services.AddSingleton(factory);
        services.AddSingleton<ITool, AskHumanTool>();
        return services;
    }

    // ── Builder extensions ───────────────────────────────────────────────────

    public static ModelHarnessBuilder WithFileSkillStore(this ModelHarnessBuilder builder, string directory) =>
        builder.WithSkillStore(_ => new FileSkillStore(directory));

    public static ModelHarnessBuilder WithSkillTools(this ModelHarnessBuilder builder)
    {
        builder.Services.AddSingleton<ITool, SkillManageTool>();
        builder.Services.AddSingleton<ITool, SkillViewTool>();
        return builder;
    }

    public static ModelHarnessBuilder WithAskHumanTool<TChannel>(this ModelHarnessBuilder builder)
        where TChannel : class, IHumanChannel
    {
        builder.Services.AddSingleton<IHumanChannel, TChannel>();
        builder.Services.AddSingleton<ITool, AskHumanTool>();
        return builder;
    }

    public static ModelHarnessBuilder WithAskHumanTool(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IHumanChannel> factory)
    {
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<ITool, AskHumanTool>();
        return builder;
    }

    public static ModelHarnessBuilder WithConsoleTracer(this ModelHarnessBuilder builder) =>
        builder.WithTracer<ConsoleTracer>();

    public static ModelHarnessBuilder WithOtelTracer(this ModelHarnessBuilder builder) =>
        builder.WithTracer<OpenTelemetryTracer>();

    // ── Layer 3: opinionated entry point ─────────────────────────────────────

    public static IServiceCollection AddStandardModelHarness(
        this IServiceCollection services,
        Action<ModelHarnessBuilder> configure) =>
        services.AddModelHarness(builder =>
        {
            builder
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithSensor<StuckDetector>()
                .WithOtelTracer();
            configure(builder);
        });
}
