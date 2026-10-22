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
        var failed = new List<Exception>();
        foreach (CachedResponse response in responses)
        {
            try
            {
                database.Write(db => CacheTagSql.Record(db, response));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The rows now exist, so the retry updates them. A response that fails again does not
                // cost the rest of the batch, but fails the call once they are written: its key is out
                // already, so the writer must try it again.
                try
                {
                    database.Write(db => CacheTagSql.Record(db, response));
                }
                catch (Exception retryException) when (retryException is not OperationCanceledException)
                {
                    logger.LogWarning(retryException, "Recording cache tag response {ResponseKey} failed.", response.ResponseKey);
                    failed.Add(retryException);
                }
            }
        }

        return failed.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new AggregateException($"Recording {failed.Count} of {responses.Count} cache tag responses failed.", failed));
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

    public Task<CacheSearchResult> SearchAsync(string text, int take, CancellationToken cancellationToken = default)
        => Task.FromResult(database.Read(db => CacheTagSql.Search(db, text, take)));

    // Each step in its own short transaction, so a publish waiting for the write lock is not held up
    // by one long delete. A render can skip rewriting keys written within the writer's touch interval,
    // so a dropped key gets that much longer.
    public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(
            database.Write(db => CacheTagSql.PruneDroppedKeys(db, cutoffUtc - MappingWriter.TouchInterval))
            + database.Write(db => CacheTagSql.PruneUnseenKeys(db, cutoffUtc))
            + database.Write(db => CacheTagSql.PruneUnseenResponses(db, cutoffUtc)));
}
