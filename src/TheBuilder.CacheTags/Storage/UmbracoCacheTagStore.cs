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
        var failed = new List<string>();
        var failures = new List<Exception>();
        var inARow = 0;
        foreach (CachedResponse response in responses)
        {
            // Two responses in a row failed twice each: the database is down, so the rest are not tried.
            Exception? failure = inARow >= 2 ? null : Record(response);
            if (inARow >= 2 || failure is not null)
            {
                failed.Add(response.ResponseKey);
                if (failure is not null)
                {
                    failures.Add(failure);
                    inARow++;
                }

                continue;
            }

            inARow = 0;
        }

        // The responses are out with their keys already, so the writer must try the failed ones again.
        return failed.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new PartialRecordException(failed, failures));
    }

    // The rows a failed attempt inserted exist now, so the retry updates them: when another server
    // inserts the same row at the same moment, the unique index rejects one insert, and the retry wins.
    private Exception? Record(CachedResponse response)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                database.Write(db => CacheTagSql.Record(db, response));
                return null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (attempt == 2)
                {
                    logger.LogWarning(exception, "Recording cache tag response {ResponseKey} failed.", response.ResponseKey);
                    return exception;
                }
            }
        }
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

    // A transaction per chunk, so a publish waiting for the write lock is not held up by a long delete.
    public Task<int> ForgetAsync(IReadOnlyCollection<string> responseKeys, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var chunk in responseKeys.Distinct(StringComparer.Ordinal).Chunk(CacheTagSql.ChunkSize))
        {
            removed += database.Write(db => CacheTagSql.Forget(db, chunk, cutoffUtc));
        }

        return Task.FromResult(removed);
    }

    // Each step in its own short transaction, so a publish waiting for the write lock is not held up
    // by one long delete.
    public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(
            database.Write(db => CacheTagSql.PruneDroppedKeys(db, cutoffUtc))
            + database.Write(db => CacheTagSql.PruneUnseenKeys(db, cutoffUtc))
            + database.Write(db => CacheTagSql.PruneUnseenResponses(db, cutoffUtc)));
}
