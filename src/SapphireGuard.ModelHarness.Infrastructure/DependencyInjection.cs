using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>Configures a single skill store for both reading and writing. Use <see cref="WithLearning"/> and <see cref="WithSkills"/> for the two-store pattern.</summary>
    public static ModelHarnessBuilder WithSkillStore(this ModelHarnessBuilder builder, string directory)
    {
        builder.WithSkillStore(_ => new FileSkillStore(directory));
        AddSkillTools(builder);
        return builder;
    }

    /// <summary>Enables agent learning: a writable store where the agent saves procedures it works out at runtime. Registers <c>skill_manage</c> and <c>skill_view</c> automatically.</summary>
    public static ModelHarnessBuilder WithLearning(this ModelHarnessBuilder builder, string directory)
    {
        GetOrAddSkillConfig(builder).AgentDirectory = directory;
        AddSkillTools(builder);
        return builder;
    }

    /// <summary>Provides pre-authored skills the agent can read but not modify. Registers <c>skill_view</c> automatically. Chain with <see cref="WithLearning"/> to enable both.</summary>
    public static ModelHarnessBuilder WithSkills(this ModelHarnessBuilder builder, string directory)
    {
        GetOrAddSkillConfig(builder).UserDirectories.Add(directory);
        AddSkillViewTool(builder);
        return builder;
    }

    /// <summary>
    /// Registers <c>ask_human</c> backed by <typeparamref name="TNotifier"/> as the human-input notifier.
    /// Use this for development with <c>ConsoleHumanChannel</c> or to inject a notifier registered elsewhere in DI.
    /// </summary>
    public static ModelHarnessBuilder WithAskHumanTool<TNotifier>(this ModelHarnessBuilder builder)
        where TNotifier : class, IHumanNotifier
    {
        builder.Services.AddSingleton<IHumanNotifier, TNotifier>();
        builder.Services.AddSingleton<ITool, AskHumanTool>();
        return builder;
    }

    /// <summary>
    /// Registers <c>ask_human</c> backed by the <see cref="IHumanNotifier"/> returned by
    /// <paramref name="factory"/>. Use this when the channel requires runtime configuration
    /// (e.g. a Slack client with a specific channel ID).
    /// </summary>
    public static ModelHarnessBuilder WithAskHumanTool(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IHumanNotifier> factory)
    {
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<ITool, AskHumanTool>();
        return builder;
    }

    /// <summary>Adds a <c>ConsoleTracer</c> that writes human-readable trace output to stdout. Handy for local development.</summary>
    public static ModelHarnessBuilder WithConsoleTracer(this ModelHarnessBuilder builder) =>
        builder.WithTracer<ConsoleTracer>();

    /// <summary>Adds an <c>OpenTelemetryTracer</c> that emits spans and metrics via the OpenTelemetry SDK.</summary>
    public static ModelHarnessBuilder WithOtelTracer(this ModelHarnessBuilder builder) =>
        builder.WithTracer<OpenTelemetryTracer>();

    // ── Layer 3: opinionated entry point ─────────────────────────────────────

    /// <summary>
    /// Registers the model harness with opinionated defaults: <c>InMemoryToolRegistry</c>,
    /// <c>StuckDetector</c>, <c>ProgressCheckSensor</c>, <c>PromptInjectionSensor</c>,
    /// and OpenTelemetry tracing. Pass the <paramref name="configure"/> callback to add
    /// your model, tools, and any overrides.
    /// </summary>
    public static IServiceCollection AddStandardModelHarness(
        this IServiceCollection services,
        Action<ModelHarnessBuilder> configure) =>
        services.AddModelHarness(builder =>
        {
            builder
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithSensor<StuckDetector>()
                .WithSensor<ProgressCheckSensor>()
                .WithSensor<PromptInjectionSensor>()
                .WithOtelTracer();
            configure(builder);
        });

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
}
