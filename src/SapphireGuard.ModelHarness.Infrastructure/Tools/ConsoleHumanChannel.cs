using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

// Development-time IHumanChannel that reads from stdin.
// Replace with a channel suited to the deployment environment in production.
[ExcludeFromCodeCoverage]
public sealed class ConsoleHumanChannel : IHumanChannel
{
    public Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine($"[HUMAN INPUT REQUIRED] {question}");
        Console.Write("> ");
        return Task.FromResult(Console.ReadLine() ?? string.Empty);
    }
}
