using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Guides;

[ExcludeFromCodeCoverage]
public sealed class NullCompactionStrategy : ICompactionStrategy
{
    public Task<string> SummariseAsync(
        int evictedStepCount,
        IReadOnlyList<string> evictedContent,
        int remainingTokenBudget,
        CancellationToken ct) =>
        Task.FromResult($"[{evictedStepCount} earlier step(s) omitted — context window limit]");
}
