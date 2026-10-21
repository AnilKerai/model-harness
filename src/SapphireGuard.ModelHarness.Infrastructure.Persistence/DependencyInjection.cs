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
    /// <param name="builder">The harness builder.</param>
    /// <param name="directory">Directory holding one subdirectory per task ID.</param>
    /// <param name="retainPerTask">
    /// How many of the newest checkpoints to keep per task; older files are pruned after each save.
    /// Null (the default) keeps every checkpoint, so a task's directory grows by one trajectory-sized
    /// file per turn.
    /// </param>
    public static ModelHarnessBuilder WithFileCheckpointStore(this ModelHarnessBuilder builder, string directory, int? retainPerTask = null) =>
        builder.WithCheckpointStore(_ => new FileCheckpointStore(directory, retainPerTask));
}
