using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Tools;

[ExcludeFromCodeCoverage]
public sealed class NullHumanNotifier : IHumanNotifier
{
    public Task NotifyAsync(HumanInputRequest request, CancellationToken ct = default) =>
        Task.CompletedTask;
}
