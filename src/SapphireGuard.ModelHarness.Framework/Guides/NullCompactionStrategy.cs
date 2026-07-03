using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Default compaction: inserts a bare omission note for the evicted steps and persists nothing.
/// A stateless view — the whole evicted head is reconsidered every turn — so no model call and no
/// spend. Opt in to incremental folding with <c>AiCompactionStrategy</c> (Infrastructure).
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class NullCompactionStrategy : ICompactionStrategy
{
    public Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct) =>
        Task.FromResult(CompactionResult.OmissionNote(request.EvictedSteps.Count));
}
