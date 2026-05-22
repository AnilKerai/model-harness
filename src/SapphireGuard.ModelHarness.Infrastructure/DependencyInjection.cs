using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure;

public static class DependencyInjection
{
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
