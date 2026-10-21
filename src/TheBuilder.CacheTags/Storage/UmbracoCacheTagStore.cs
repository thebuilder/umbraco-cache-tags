using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     The default <see cref="ICacheTagStore" />: the mapping lives in the Umbraco database. Every
///     server that serves the Delivery API writes to it, so it needs write access.
/// </summary>
internal sealed class UmbracoCacheTagStore(IScopeProvider scopeProvider) : ICacheTagStore
{
    // One scope per response: when another server inserts the same row at the same moment, the
    // unique index rejects one insert, and only that response is retried rather than the batch lost.
    public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default)
    {
        foreach (CachedResponse response in responses)
        {
            try
            {
                RecordOne(response);
            }
            catch (System.Data.Common.DbException)
            {
                // The rows now exist, so the retry updates them.
                RecordOne(response);
            }
        }

        return Task.CompletedTask;
    }

    private void RecordOne(CachedResponse response)
    {
        using IScope scope = WriteScope.Create(scopeProvider);
        CacheTagSql.Record(scope.Database, response);
        scope.Complete();
    }

    public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default)
    {
        using IScope scope = WriteScope.Create(scopeProvider);
        CacheTagSql.Touch(scope.Database, responseKeys, seenUtc);
        scope.Complete();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
    {
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        return Task.FromResult(CacheTagSql.FindResponseKeys(scope.Database, keys, cultures));
    }

    public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default)
    {
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        return Task.FromResult(CacheTagSql.Get(scope.Database, responseKey));
    }

    public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        using IScope scope = WriteScope.Create(scopeProvider);
        var removed = CacheTagSql.Prune(scope.Database, cutoffUtc);
        scope.Complete();
        return Task.FromResult(removed);
    }
}
