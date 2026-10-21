using Microsoft.Extensions.Logging;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     The default <see cref="ICacheTagStore" />: the mapping lives in the Umbraco database. Every
///     server that serves the Delivery API writes to it, so it needs write access.
/// </summary>
internal sealed class UmbracoCacheTagStore(PackageDatabase database, ILogger<UmbracoCacheTagStore> logger) : ICacheTagStore
{
    // One transaction per response: when another server inserts the same row at the same moment, the
    // unique index rejects one insert, and only that response is retried rather than the batch lost.
    public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default)
    {
        foreach (CachedResponse response in responses)
        {
            try
            {
                database.Write(db => CacheTagSql.Record(db, response));
            }
            catch (System.Data.Common.DbException)
            {
                // The rows now exist, so the retry updates them. A response that fails again is
                // skipped, so it does not cost the rest of the batch.
                try
                {
                    database.Write(db => CacheTagSql.Record(db, response));
                }
                catch (System.Data.Common.DbException exception)
                {
                    logger.LogWarning(exception, "Recording cache tag response {ResponseKey} failed; it is recorded on its next render.", response.ResponseKey);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default)
    {
        database.Write(db => CacheTagSql.Touch(db, responseKeys, seenUtc));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
        => Task.FromResult(database.Read(db => CacheTagSql.FindResponseKeys(db, keys, cultures)));

    public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default)
        => Task.FromResult(database.Read(db => CacheTagSql.Get(db, responseKey)));

    public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(database.Write(db => CacheTagSql.Prune(db, cutoffUtc)));
}
