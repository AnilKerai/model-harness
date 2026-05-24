using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static ModelHarnessBuilder WithFileCheckpointStore(this ModelHarnessBuilder builder, string directory) =>
        builder.WithCheckpointStore(_ => new FileCheckpointStore(directory));
}
