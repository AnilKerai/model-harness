using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;

namespace SapphireGuard.ModelHarness.Infrastructure.Persistence;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>
    /// Registers a <see cref="FileCheckpointStore"/> that persists checkpoints as JSON files
    /// under <paramref name="directory"/>. One subdirectory per task ID is created automatically.
    /// Implement <see cref="Framework.Persistence.ICheckpointStore"/> to target a different backend.
    /// </summary>
    public static ModelHarnessBuilder WithFileCheckpointStore(this ModelHarnessBuilder builder, string directory) =>
        builder.WithCheckpointStore(_ => new FileCheckpointStore(directory));
}
