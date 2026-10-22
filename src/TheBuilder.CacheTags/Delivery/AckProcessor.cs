using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Turns the cache refreshes this server applies into acknowledgements: for each unreleased change
///     a refresh completes, evict the change's responses from this server's output cache, then
///     acknowledge it. A refresh can arrive before its change is recorded (the server making a
///     background change writes its cache instructions first), so unmatched refreshes are kept for a
///     short while. A change released without this server, after the ack timeout, is still evicted
///     here when its refresh comes late, though no longer acknowledged.
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

    // How long before its change a refresh may be seen and still be the change's own. On the server
    // making the change, Umbraco can refresh a few milliseconds before it raises the change. On
    // another server, the refresh comes after the change in real time, so this only covers how far
    // its clock is behind the recording server's, which NTP keeps well under a second. A server
    // further behind is not acknowledged in time, so its changes wait for the ack timeout: a delay,
    // logged, rather than a purge sent before it has the change.
    public static readonly TimeSpan EarlySlack = TimeSpan.FromSeconds(2);

    // How long after a change is released without this server a late refresh of it still evicts its
    // responses here. Minutes, not the ack timeout: a server whose instruction processing stalled can
    // apply a change long after it was released.
    private static readonly TimeSpan LateWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentQueue<(DateTime ObservedUtc, ObservedRefresh Refresh)> _queue = new();
    private readonly ConcurrentDictionary<Guid, byte> _acknowledged = new();
    private readonly ConcurrentDictionary<Guid, byte> _evictedLate = new();

    public bool HasQueued => _queue.IsEmpty is false;

    /// <summary>
    ///     Queues refreshes this server just applied. No database work happens here: this runs inside
    ///     Umbraco's cache refresh, and the delivery worker processes the queue moments later.
    /// </summary>
    public void Observe(IReadOnlyCollection<ObservedRefresh> refreshes)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (ObservedRefresh refresh in refreshes)
        {
            _queue.Enqueue((now, refresh));
        }

        signal.Wake();
    }

    /// <summary>
    ///     Acknowledges the changes the queued refreshes complete, evicts the late ones released
    ///     without this server, and keeps the other refreshes while they are recent.
    /// </summary>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var queued = new List<(DateTime ObservedUtc, ObservedRefresh Refresh)>();
        while (_queue.TryDequeue(out var item))
        {
            queued.Add(item);
        }

        if (queued.Count == 0)
        {
            return;
        }

        // By reference: two identical refreshes are still two refreshes.
        var used = new HashSet<ObservedRefresh>(ReferenceEqualityComparer.Instance);
        try
        {
            IReadOnlyList<LedgerEntry> unreleased = ledger.InStage(ChangeStage.Pending, 500);
            foreach (Guid released in _acknowledged.Keys.Except(unreleased.Select(change => change.Id)))
            {
                _acknowledged.TryRemove(released, out _);
            }

            IReadOnlyList<LedgerEntry> missed = ledger.ReleasedWithout(identity.Name, time.GetUtcNow().UtcDateTime - LateWindow, 100);
            foreach (Guid aged in _evictedLate.Keys.Except(missed.Select(change => change.Id)))
            {
                _evictedLate.TryRemove(aged, out _);
            }

            IEnumerable<LedgerEntry> open = unreleased
                .Where(change => _acknowledged.ContainsKey(change.Id) is false)
                .Concat(missed.Where(change => _evictedLate.ContainsKey(change.Id) is false))
                .OrderBy(change => change.CreatedUtc);
            var matched = Match(open, queued, used).ToList();

            List<LedgerEntry> late = matched.Where(change => change.Stage != ChangeStage.Pending).ToList();
            if (late.Count > 0)
            {
                await EvictLateAsync(late, cancellationToken);
            }

            List<LedgerEntry> completed = matched.Where(change => change.Stage == ChangeStage.Pending).ToList();
            if (completed.Count > 0)
            {
                await AcknowledgeAsync(completed, cancellationToken);
            }
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
            foreach (var item in queued.Where(item => used.Contains(item.Refresh) is false && now - item.ObservedUtc < RetryWindow))
            {
                _queue.Enqueue(item);
            }
        }
    }

    /// <summary>
    ///     The changes the refreshes complete, oldest first, adding the refreshes that completed one to
    ///     <paramref name="used" />. A refresh applied after a change reloads its items, so it brings
    ///     this server up to date with every matching change made before it, and may complete several.
    ///     A refresh seen just before a change (see <see cref="EarlySlack" />) can be the change's own,
    ///     but it completes that one change only, and none if it already completed an earlier change: a
    ///     late refresh of an earlier change to the same item must not count for a newer one.
    /// </summary>
    public static IEnumerable<LedgerEntry> Match(IEnumerable<LedgerEntry> changes, IReadOnlyList<(DateTime ObservedUtc, ObservedRefresh Refresh)> refreshes, ISet<ObservedRefresh> used)
    {
        foreach (LedgerEntry change in changes)
        {
            if (change.Signal is not { } signal)
            {
                continue;
            }

            var candidates = refreshes.Where(item => signal.IsMetBy(item.Refresh)).ToList();
            var match = candidates.FirstOrDefault(item => item.ObservedUtc >= change.CreatedUtc);
            if (match.Refresh is null)
            {
                match = candidates.LastOrDefault(item => change.CreatedUtc - item.ObservedUtc <= EarlySlack && used.Contains(item.Refresh) is false);
            }

            if (match.Refresh is not null)
            {
                used.Add(match.Refresh);
                yield return change;
            }
        }
    }

    /// <summary>Evicts the responses of changes released without this server, now that it has applied them.</summary>
    private async Task EvictLateAsync(IReadOnlyList<LedgerEntry> late, CancellationToken cancellationToken)
    {
        if (eviction.Enabled)
        {
            foreach (LedgerEntry change in late)
            {
                // Listings too: this server is not waited for in the indexing stage, so nothing else evicts them here.
                ResolvedPurge purge = await resolver.ResolveAsync(change.Plan, cancellationToken);
                await eviction.EvictAsync(purge);
                await eviction.EvictAsync(purge.ListingOutputCacheTags);
            }
        }

        foreach (LedgerEntry change in late)
        {
            _evictedLate.TryAdd(change.Id, 0);
        }

        logger.LogInformation(
            "Cache tags: {Server} applied {Changes} after they were released without it, and evicted their responses.",
            identity.Name,
            string.Join(", ", late.Select(change => change.Id)));
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
        foreach (LedgerEntry change in completed)
        {
            _acknowledged.TryAdd(change.Id, 0);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Cache tags: {Server} acknowledged {Changes}.", identity.Name, string.Join(", ", completed.Select(change => change.Id)));
        }
    }
}
