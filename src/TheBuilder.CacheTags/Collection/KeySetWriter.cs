using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Stores the set of items a response shows when they are too many for its header, so the
///     response can carry one <c>set-</c> key for them. Only a stored set may be carried: a change
///     finds the sets to purge in the database. Anything that keeps the set from being stored in time
///     (the database is slow or failing, the package is not installed yet, too many new sets this
///     minute) leaves the response to carry its items' types, as it would without sets.
/// </summary>
internal sealed class KeySetWriter(KeySetStore store, PackageDatabase database, TimeProvider time, ILogger<KeySetWriter> logger)
{
    /// <summary>How long a response waits for its set to be stored before it carries its items' types instead.</summary>
    public static readonly TimeSpan WriteBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>The most sets a server stores a minute, so a flood of distinct requests cannot fill the table.</summary>
    public const int MaxNewSetsPerMinute = 100;

    /// <summary>
    ///     How long a server trusts a set it stored without storing it again. Storing it again marks it
    ///     seen, which keeps it from being pruned, so this is how often a server refreshes that.
    /// </summary>
    public static readonly TimeSpan RememberFor = TimeSpan.FromDays(1);

    // The most sets remembered; past it the memory starts over, which only costs a write each.
    private const int MaxRemembered = 10_000;

    private readonly ConcurrentDictionary<string, DateTime> _stored = new(StringComparer.Ordinal);
    private readonly Lock _window = new();
    private DateTime _windowStartUtc = DateTime.MinValue;
    private int _writesInWindow;
    private bool _loggedCap;
    private bool _loggedFailure;

    /// <summary>
    ///     The <c>set-</c> key of these items once their set is stored, or <c>null</c> when it could not
    ///     be in time, and the response must carry their types.
    /// </summary>
    public async Task<string?> TryStoreAsync(IReadOnlyList<string> items)
    {
        var key = KeySetStore.KeyOf(items);
        DateTime now = time.GetUtcNow().UtcDateTime;
        if (_stored.TryGetValue(key, out DateTime storedUtc) && now - storedUtc < RememberFor)
        {
            return key;
        }

        if (database.IsInstalled() is false || TakeWrite(now) is false)
        {
            return null;
        }

        Task write;

        // Not in the request's ambient scope, if it has one: the write commits on its own, and keeps
        // running when the request stops waiting for it.
        using (ExecutionContext.SuppressFlow())
        {
            write = Task.Run(() => store.Add(items, now));
        }

        try
        {
            await write.WaitAsync(WriteBudget, time);
        }
        catch (TimeoutException)
        {
            // The write may still finish: a later response showing the same items finds it stored.
            logger.LogDebug("Cache tags: a key set was not stored within {Budget}; the response carries its items' types.", WriteBudget);
            _ = write.ContinueWith(
                finished => Remember(key, finished.Exception is null, now),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }
        catch (Exception exception)
        {
            if (FirstFailureThisMinute())
            {
                logger.LogWarning(exception, "Cache tags: a key set could not be stored; until it can, responses showing too many items to list carry their items' types.");
            }

            return null;
        }

        Remember(key, stored: true, now);
        return key;
    }

    private void Remember(string key, bool stored, DateTime storedUtc)
    {
        if (stored is false)
        {
            return;
        }

        if (_stored.Count >= MaxRemembered)
        {
            _stored.Clear();
        }

        _stored[key] = storedUtc;
    }

    // Whether a failed write is the first since the minute TakeWrite started, so a database that is
    // down logs once a minute.
    private bool FirstFailureThisMinute()
    {
        lock (_window)
        {
            var first = _loggedFailure is false;
            _loggedFailure = true;
            return first;
        }
    }

    // Counts a write against this minute's cap. Returns whether it may go ahead.
    private bool TakeWrite(DateTime now)
    {
        lock (_window)
        {
            if (now - _windowStartUtc >= TimeSpan.FromMinutes(1))
            {
                _windowStartUtc = now;
                _writesInWindow = 0;
                _loggedCap = false;
                _loggedFailure = false;
            }

            if (_writesInWindow >= MaxNewSetsPerMinute)
            {
                if (_loggedCap is false)
                {
                    _loggedCap = true;
                    logger.LogWarning(
                        "Cache tags stored {Max} key sets on this server in the last minute; until the minute is up, responses showing too many items to list carry their items' types.",
                        MaxNewSetsPerMinute);
                }

                return false;
            }

            _writesInWindow++;
            return true;
        }
    }
}
