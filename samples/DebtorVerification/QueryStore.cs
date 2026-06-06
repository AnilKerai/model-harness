using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Samples.DebtorVerification;

[ExcludeFromCodeCoverage]
public sealed class QueryStore
{
    private readonly ConcurrentDictionary<string, PendingQuery> _queries = new();

    public string Submit(string queryId, string? param)
    {
        var handle = Guid.NewGuid().ToString("N")[..12];
        _queries[handle] = new PendingQuery(queryId, param);
        return handle;
    }

    public PendingQuery? TryGet(string handle)
        => _queries.TryGetValue(handle, out var q) ? q : null;
}

[ExcludeFromCodeCoverage]
public sealed record PendingQuery(string QueryId, string? Param);
