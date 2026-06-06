using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Compaction;
using SapphireGuard.ModelHarness.Infrastructure.MultiAgent;
using SapphireGuard.ModelHarness.Infrastructure.Security;
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

    /// <summary>
    /// Enables AI-powered trajectory compaction. When the context window fills up, evicted steps
    /// are summarised by <paramref name="modelClient"/> rather than silently dropped. Use a fast,
    /// cheap model (Haiku-class) to keep compaction overhead low. The strategy fails open —
    /// a bare omission note is used if the model call fails or returns empty text.
    /// </summary>
    public static ModelHarnessBuilder WithAiCompaction(
        this ModelHarnessBuilder builder,
        IModelClient modelClient)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ICompactionStrategy>(
            _ => new AiCompactionStrategy(modelClient)));
        return builder;
    }

    /// <summary>
    /// Enables AI-powered trajectory compaction using a model client resolved from the container.
    /// Use a fast, cheap model (Haiku-class) to keep compaction overhead low.
    /// </summary>
    public static ModelHarnessBuilder WithAiCompaction(
        this ModelHarnessBuilder builder,
        Func<IServiceProvider, IModelClient> factory)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ICompactionStrategy>(
            sp => new AiCompactionStrategy(factory(sp))));
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
            ApplyStandardDefaults(builder);
            configure(builder);
        });

    /// <summary>
    /// Applies the standard opinionated defaults to any <see cref="ModelHarnessBuilder"/>.
    /// Single source of truth shared by <see cref="AddStandardModelHarness"/> and
    /// <see cref="AgentFactory.AddStandardAgent"/> — add new standard sensors here only.
    /// </summary>
    internal static void ApplyStandardDefaults(ModelHarnessBuilder builder) =>
        builder
            .WithToolRegistry<InMemoryToolRegistry>()
            .WithTool(_ => new GetDateTimeTool(TimeProvider.System))
            .WithSensor<StuckDetector>()
            .WithSensor<ProgressCheckSensor>()
            .WithSensor<PromptInjectionSensor>()
            .WithOtelTracer();

    // ── Security ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Enables trajectory-level taint tracking. Tool results from <paramref name="untrustedSources"/>
    /// are flagged as tainted; subsequent calls to <paramref name="privilegedActions"/> are blocked
    /// while any tainted step remains in the trajectory. MCP tools and any remote tool whose author
    /// cannot be verified should be listed as untrusted sources.
    /// </summary>
    public static ModelHarnessBuilder WithTaintTracking(
        this ModelHarnessBuilder builder,
        IEnumerable<string> untrustedSources,
        IEnumerable<string> privilegedActions)
    {
        var policy = new TrustPolicy(untrustedSources, privilegedActions);
        builder.Services.Replace(ServiceDescriptor.Singleton<ITrustPolicy>(policy));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISensor, TaintTrackingSensor>());
        return builder;
    }

    // ── Multi-agent ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AgentFactory"/>, passes it to <paramref name="configure"/> so
    /// named agents can be registered, then adds the factory as a singleton so it can be
    /// resolved and disposed by the host container.
    /// </summary>
    public static IServiceCollection AddAgentFactory(
        this IServiceCollection services,
        Action<AgentFactory> configure)
    {
        var factory = new AgentFactory();
        configure(factory);
        services.AddSingleton(factory);
        return services;
    }

    /// <summary>
    /// Registers the named sub-agent as a tool on this agent's harness. The
    /// <paramref name="factory"/> reference is captured directly in a closure —
    /// no service resolution from the sub-container is needed.
    /// </summary>
    public static ModelHarnessBuilder AddSubAgentAsTool(
        this ModelHarnessBuilder builder,
        string agentName,
        AgentFactory factory) =>
        builder.WithTool(_ => new AgentTool(agentName, factory));

    // ── Internal helpers ─────────────────────────────────────────────────────

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
