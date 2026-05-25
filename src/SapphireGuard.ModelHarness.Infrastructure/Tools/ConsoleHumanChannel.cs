using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

// Development-time IHumanNotifier that prints the question to stdout then returns immediately.
// The harness suspends; the sample's resume loop reads the answer from stdin.
[ExcludeFromCodeCoverage]
public sealed class ConsoleHumanChannel : IHumanNotifier
{
    public Task NotifyAsync(HumanInputRequest request, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine($"[HUMAN INPUT REQUIRED] {request.Question}");
        Console.Write("> ");
        return Task.CompletedTask;
    }
}
