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
    public static ModelHarnessBuilder WithFileCheckpointStore(this ModelHarnessBuilder builder, string directory) =>
        builder.WithFileCheckpointStore(directory, null);

    /// <summary>
    /// As <see cref="WithFileCheckpointStore(ModelHarnessBuilder, string)"/>, retaining only the newest
    /// <paramref name="retainPerTask"/> checkpoints per task. A separate overload rather than a default
    /// on the parameter, which would change the signature and break callers already compiled against it.
    /// </summary>
    public static ModelHarnessBuilder WithFileCheckpointStore(this ModelHarnessBuilder builder, string directory, int? retainPerTask) =>
        builder.WithCheckpointStore(_ => new FileCheckpointStore(directory, retainPerTask));
}
