using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Skills;
using SapphireGuard.ModelHarness.Infrastructure.Tools;

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
}
