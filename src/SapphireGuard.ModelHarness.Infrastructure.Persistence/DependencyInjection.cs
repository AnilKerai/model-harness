using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Persistence;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddFileCheckpointStore(this IServiceCollection services, string directory) =>
        services.AddCheckpointStore(_ => new FileCheckpointStore(directory));
}
