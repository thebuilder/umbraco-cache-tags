using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Turns the cache refreshes this server applies into acknowledgements: for each unreleased change
///     a refresh completes, evict the change's responses from this server's output cache, then
///     acknowledge it. A refresh can arrive before its change is recorded (the server making a
///     background change writes its cache instructions first), so unmatched refreshes are kept for a
///     short while.
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

    private readonly ConcurrentQueue<(DateTime ObservedUtc, ObservedRefresh Refresh)> _queue = new();
    private readonly ConcurrentDictionary<Guid, byte> _acknowledged = new();

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

    /// <summary>Acknowledges the changes the queued refreshes complete, and keeps the rest while they are recent.</summary>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        var queued = new List<(DateTime ObservedUtc, ObservedRefresh Refresh)>();
        while (_queue.TryDequeue(out var item))
        {
            if (now - item.ObservedUtc < RetryWindow)
            {
                queued.Add(item);
            }
        }

        if (queued.Count == 0)
        {
            return;
        }

        var refreshes = queued.Select(item => item.Refresh).ToList();
        var used = new HashSet<ObservedRefresh>();
        var completed = new List<LedgerEntry>();
        foreach (LedgerEntry change in ledger.Unreleased(500).Where(change => _acknowledged.ContainsKey(change.Id) is false))
        {
            ObservedRefresh? refresh = change.Signal is null ? null : refreshes.FirstOrDefault(change.Signal.IsMetBy);
            if (refresh is not null)
            {
                used.Add(refresh);
                completed.Add(change);
            }
        }

        foreach (var item in queued.Where(item => used.Contains(item.Refresh) is false))
        {
            _queue.Enqueue(item);
        }

        if (completed.Count > 0)
        {
            await AcknowledgeAsync(completed, cancellationToken);
        }
    }

    /// <summary>Forgets a change once it is released, so the set does not grow forever.</summary>
    public void Forget(Guid changeId) => _acknowledged.TryRemove(changeId, out _);

    private async Task AcknowledgeAsync(IReadOnlyList<LedgerEntry> changes, CancellationToken cancellationToken)
    {
        if (eviction.Enabled)
        {
            foreach (LedgerEntry change in changes)
            {
                await eviction.EvictAsync(await resolver.ResolveAsync(change.Plan, cancellationToken));
            }
        }

        await transport.AckAsync(identity.Name, changes.Select(change => change.Id).ToList(), time.GetUtcNow().UtcDateTime, cancellationToken);
        foreach (LedgerEntry change in changes)
        {
            _acknowledged.TryAdd(change.Id, 0);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Cache tags: {Server} acknowledged {Changes}.", identity.Name, string.Join(", ", changes.Select(change => change.Id)));
        }
    }
}
