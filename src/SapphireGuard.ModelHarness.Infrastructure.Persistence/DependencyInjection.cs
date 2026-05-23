using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static IServiceCollection AddFileCheckpointStore(this IServiceCollection services, string directory) =>
        services.AddCheckpointStore(_ => new FileCheckpointStore(directory));
}
