using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public static ModelHarnessBuilder WithFileSkillStore(this ModelHarnessBuilder builder, string directory)
    {
        builder.WithSkillStore(_ => new FileSkillStore(directory));
        AddSkillTools(builder);
        return builder;
    }

    public static ModelHarnessBuilder WithAgentSkillStore(this ModelHarnessBuilder builder, string directory)
    {
        GetOrAddSkillConfig(builder).AgentDirectory = directory;
        AddSkillTools(builder);
        return builder;
    }

    public static ModelHarnessBuilder WithUserSkillStore(this ModelHarnessBuilder builder, string directory)
    {
        GetOrAddSkillConfig(builder).UserDirectories.Add(directory);
        AddSkillViewTool(builder);
        return builder;
    }

    private static SkillStoreConfiguration GetOrAddSkillConfig(ModelHarnessBuilder builder)
    {
        var existing = builder.Services
            .FirstOrDefault(d => d.ServiceType == typeof(SkillStoreConfiguration))
            ?.ImplementationInstance as SkillStoreConfiguration;
        if (existing is not null) return existing;

        var config = new SkillStoreConfiguration();
        builder.Services.AddSingleton(config);
        builder.WithSkillStore(sp => sp.GetRequiredService<SkillStoreConfiguration>().Build());
        return config;
    }

    private static void AddSkillTools(ModelHarnessBuilder builder)
    {
        AddSkillViewTool(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITool, SkillManageTool>());
    }

    private static void AddSkillViewTool(ModelHarnessBuilder builder) =>
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITool, SkillViewTool>());

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
