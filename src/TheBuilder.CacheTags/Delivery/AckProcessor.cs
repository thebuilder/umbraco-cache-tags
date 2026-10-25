using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Turns the cache refreshes this server applies into acknowledgements: for each unreleased change
///     a refresh completes, evict the change's responses from this server's output cache, then
///     acknowledge it. A refresh can arrive before its change is recorded (the server making a
///     background change writes its cache instructions first), so unmatched refreshes are kept for a
///     short while. A change released without this server, after the ack timeout, is evicted here
///     once a refresh of it has been seen, before or after the release, though not acknowledged.
/// </summary>
internal sealed class AckProcessor(
    LedgerStore ledger,
    PurgeResolver resolver,
    OutputCacheEviction eviction,
    IAckTransport transport,
    ServerIdentity identity,
    DeliverySignal signal,
    TimeProvider time,
    ILogger<AckProcessor> logger)
{
    // The gap between a refresh and the change it belongs to being recorded is milliseconds; keeping
    // it short also keeps an unrelated earlier refresh from acknowledging a later change.
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(10);

    // How far apart a refresh and a change may be seen and the refresh still be the change's own, or
    // an earlier one. On the server making the change, Umbraco can refresh a few milliseconds before
    // it raises the change. On another server the refresh comes after the change, but its clock can
    // be behind or ahead of the recording server's, which NTP keeps well under a second. A refresh
    // this close to a change counts for that one change only. A server more than this far behind is
    // not acknowledged in time: its changes wait for the ack timeout, and it evicts them once released.
    public static readonly TimeSpan EarlySlack = TimeSpan.FromSeconds(2);

    // How long after a change is released without this server a refresh of it still evicts its
    // responses here, and how long refreshes are kept for that. Minutes, not the ack timeout: a server
    // whose instruction processing stalled can apply a change long after it was released.
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(15);

    // How often changes released without this server are looked for, while it has seen refreshes.
    private static readonly TimeSpan LateInterval = TimeSpan.FromSeconds(5);

    // The most refreshes kept for late evictions; a bulk publish refreshes thousands of items.
    private const int HistoryLimit = 10_000;

    private readonly ConcurrentQueue<SeenRefresh> _queue = new();
    private readonly ConcurrentQueue<SeenRefresh> _history = new();

    // The notifications whose refreshes completed a change: their other refreshes no longer count
    // for a change seen close to them. Only the delivery worker reads and writes these three.
    private readonly HashSet<long> _usedBatches = [];
    private readonly Dictionary<Guid, long> _evictedLate = [];
    private DateTime _lateCheckedUtc = DateTime.MinValue;

    // The changes this server acknowledged, and when: a change can be released without its ack
    // having been seen, and must not be evicted again as late.
    private readonly ConcurrentDictionary<Guid, DateTime> _acknowledged = new();
    private long _batches;
    private long _sequence;

    public bool HasQueued => _queue.IsEmpty is false;

    /// <summary>
    ///     Queues refreshes this server just applied, from one cache refresher notification. No
    ///     database work happens here: this runs inside Umbraco's cache refresh, and the delivery worker
    ///     processes the queue moments later.
    /// </summary>
    public void Observe(IReadOnlyCollection<ObservedRefresh> refreshes)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        var batch = Interlocked.Increment(ref _batches);
        foreach (ObservedRefresh refresh in refreshes)
        {
            var seen = new SeenRefresh(Interlocked.Increment(ref _sequence), now, batch, refresh);
            _queue.Enqueue(seen);
            _history.Enqueue(seen);
        }

        while (_history.Count > HistoryLimit && _history.TryDequeue(out _))
        {
        }

        signal.Wake();
    }

    /// <summary>
    ///     Acknowledges the pending changes the queued refreshes complete, keeping the other refreshes
    ///     while they are recent, then evicts the changes released without this server that a refresh
    ///     seen here belongs to.
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (var acknowledged in _acknowledged.Where(pair => now - pair.Value > LateWindow))
        {
            _acknowledged.TryRemove(acknowledged.Key, out _);
        }

        await AcknowledgeQueuedAsync(cancellationToken);
        await EvictLateAsync(now, cancellationToken);
    }

    private async Task AcknowledgeQueuedAsync(CancellationToken cancellationToken)
    {
        var queued = new List<SeenRefresh>();
        while (_queue.TryDequeue(out SeenRefresh item))
        {
            queued.Add(item);
        }

        if (queued.Count == 0)
        {
            return;
        }

        // By reference: two identical refreshes are still two refreshes.
        var used = new HashSet<ObservedRefresh>(ReferenceEqualityComparer.Instance);
        var usedBatches = new HashSet<long>(_usedBatches);
        try
        {
            IEnumerable<LedgerEntry> unreleased = ledger.InStage(ChangeStage.Pending, 500)
                .Where(change => _acknowledged.ContainsKey(change.Id) is false);
            var completed = Match(unreleased, queued, used, usedBatches).ToList();
            if (completed.Count > 0)
            {
                await AcknowledgeAsync(completed, cancellationToken);
            }

            _usedBatches.UnionWith(usedBatches);
        }
        catch
        {
            // Nothing was acknowledged: every refresh is tried again on the next pass.
            used.Clear();
            throw;
        }
        finally
        {
            // A refresh that completed nothing is kept for a while: its change may not be recorded yet.
            DateTime now = time.GetUtcNow().UtcDateTime;
            foreach (SeenRefresh item in queued.Where(item => used.Contains(item.Refresh) is false && now - item.ObservedUtc < RetryWindow))
            {
                _queue.Enqueue(item);
            }

            var waiting = _queue.Select(item => item.Batch).ToHashSet();
            _usedBatches.RemoveWhere(batch => waiting.Contains(batch) is false);
        }
    }

    /// <summary>
    ///     The changes the refreshes complete, oldest first, adding the refreshes that completed one to
    ///     <paramref name="used" /> and their notifications to <paramref name="usedBatches" />.
    ///     A refresh seen well after a change (more than <see cref="EarlySlack" />) reloaded its items
    ///     after it, so it brings this server up to date with every matching change made before it,
    ///     and may complete several. One seen within <see cref="EarlySlack" /> of a change, either
    ///     side, can be the change's own or, with clocks apart, an earlier one: it completes one change
    ///     only, and none once its notification completed another. One seen well before a change never
    ///     counts: a late refresh of an earlier change to the same item must not count for a newer one.
    /// </summary>
    public static IEnumerable<LedgerEntry> Match(IEnumerable<LedgerEntry> changes, IReadOnlyList<SeenRefresh> refreshes, ISet<ObservedRefresh> used, ISet<long> usedBatches)
    {
        foreach (LedgerEntry change in changes)
        {
            if (change.Signal is not { } signal)
            {
                continue;
            }

            var candidates = refreshes.Where(item => signal.IsMetBy(item.Refresh)).ToList();
            SeenRefresh? match = candidates.Cast<SeenRefresh?>().FirstOrDefault(item => item!.Value.ObservedUtc - change.CreatedUtc > EarlySlack);
            match ??= candidates
                .Where(item => (item.ObservedUtc - change.CreatedUtc).Duration() <= EarlySlack)
                .Where(item => used.Contains(item.Refresh) is false && usedBatches.Contains(item.Batch) is false)
                .OrderBy(item => (item.ObservedUtc - change.CreatedUtc).Duration())
                .Cast<SeenRefresh?>()
                .FirstOrDefault();

            if (match is { } found)
            {
                used.Add(found.Refresh);
                usedBatches.Add(found.Batch);
                yield return change;
            }
        }
    }

    /// <summary>
    ///     Evicts the responses and listings of changes released without this server, after the ack
    ///     timeout, once a refresh of them has been seen here, before or after the release: this server
    ///     has applied them, and nothing else evicts them here. Every new matching refresh evicts again,
    ///     with no rule on when it was seen: evicting early costs a cache miss, and the change's own
    ///     refresh, whenever it comes, evicts after it.
    /// </summary>
    private async Task EvictLateAsync(DateTime now, CancellationToken cancellationToken)
    {
        while (_history.TryPeek(out SeenRefresh oldest) && now - oldest.ObservedUtc > LateWindow)
        {
            _history.TryDequeue(out _);
        }

        if (_history.IsEmpty || now - _lateCheckedUtc < LateInterval)
        {
            return;
        }

        _lateCheckedUtc = now;
        var missed = ledger.ReleasedWithout(identity.Name, now - LateWindow, 100)
            .Where(change => _acknowledged.ContainsKey(change.Id) is false && change.Signal is not null)
            .ToList();
        foreach (Guid gone in _evictedLate.Keys.Except(missed.Select(change => change.Id)).ToList())
        {
            _evictedLate.Remove(gone);
        }

        SeenRefresh[] history = _history.ToArray();
        var late = new List<(LedgerEntry Change, long Newest)>();
        foreach (LedgerEntry change in missed)
        {
            var handled = _evictedLate.GetValueOrDefault(change.Id);
            var newest = history.Where(item => item.Sequence > handled && change.Signal!.IsMetBy(item.Refresh)).Select(item => item.Sequence).DefaultIfEmpty().Max();
            if (newest > 0)
            {
                late.Add((change, newest));
            }
        }

        if (late.Count == 0)
        {
            return;
        }

        if (eviction.Enabled)
        {
            foreach (var (change, _) in late)
            {
                // Listings too: this server is not waited for in the indexing stage, so nothing else evicts them here.
                ResolvedPurge purge = await resolver.ResolveAsync(change.Plan, cancellationToken);
                await eviction.EvictAsync(purge);
                await eviction.EvictAsync(purge.ListingOutputCacheTags);
            }
        }

        foreach (var (change, newest) in late)
        {
            _evictedLate[change.Id] = newest;
        }

        logger.LogInformation(
            "Cache tags: {Server} applied {Changes} after they were released without it, and evicted their responses.",
            identity.Name,
            string.Join(", ", late.Select(item => item.Change.Id)));
    }

    private async Task AcknowledgeAsync(IReadOnlyList<LedgerEntry> completed, CancellationToken cancellationToken)
    {
        if (eviction.Enabled)
        {
            foreach (LedgerEntry change in completed)
            {
                await eviction.EvictAsync(await resolver.ResolveAsync(change.Plan, cancellationToken));
            }
        }

        await transport.AckAsync(identity.Name, completed.Select(change => change.Id).ToList(), time.GetUtcNow().UtcDateTime, cancellationToken);
        DateTime acknowledged = time.GetUtcNow().UtcDateTime;
        foreach (LedgerEntry change in completed)
        {
            _acknowledged.TryAdd(change.Id, acknowledged);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Cache tags: {Server} acknowledged {Changes}.", identity.Name, string.Join(", ", completed.Select(change => change.Id)));
        }
    }
}

/// <summary>A refresh this server applied: its place in order, when, and the notification it came in.</summary>
internal readonly record struct SeenRefresh(long Sequence, DateTime ObservedUtc, long Batch, ObservedRefresh Refresh);
