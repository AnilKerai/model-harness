using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Resilience;

public static class DependencyInjection
{
    public static IServiceCollection AddResilientTool<TImpl>(this IServiceCollection services)
        where TImpl : class, ITool
    {
        services.AddSingleton<TImpl>();
        services.AddSingleton<ITool>(sp => new ResilientTool(sp.GetRequiredService<TImpl>()));
        return services;
    }
}
