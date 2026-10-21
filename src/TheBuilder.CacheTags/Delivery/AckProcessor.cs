using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Runs on every server after it applies a cache refresh: finds the unreleased changes the
///     refresh completes, evicts their responses from this server's Delivery API output cache, and
///     acknowledges them. A refresh can arrive before its change is recorded (the server making a
///     background change writes its cache instructions first), so unmatched refreshes are tried
///     again for a while.
/// </summary>
internal sealed class AckProcessor(
    IScopeProvider scopeProvider,
    IServiceScopeFactory scopeFactory,
    IAckTransport transport,
    ServerIdentity identity,
    DeliverySignal signal,
    TimeProvider time,
    ILogger<AckProcessor> logger)
{
    // The gap between a refresh and the change it belongs to being recorded is milliseconds; keeping
    // it short also keeps an unrelated earlier refresh from acknowledging a later change.
    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<(DateTime ObservedUtc, ObservedRefresh Refresh)> _unmatched = new();
    private readonly ConcurrentDictionary<Guid, byte> _acknowledged = new();

    /// <summary>
    ///     Queues refreshes this server just applied. No database work happens here: this runs inside
    ///     Umbraco's cache refresh, and the delivery worker matches the queue moments later.
    /// </summary>
    public void Observe(IReadOnlyCollection<ObservedRefresh> refreshes)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (ObservedRefresh refresh in refreshes)
        {
            _unmatched.Enqueue((now, refresh));
        }

        signal.Wake();
    }

    /// <summary>
    ///     Matches queued refreshes against the unreleased changes, evicting and acknowledging the
    ///     ones they complete. A refresh that matches nothing is kept for a short while, since it can
    ///     arrive before its change is recorded.
    /// </summary>
    public async Task ProcessAsync()
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        var queued = new List<(DateTime ObservedUtc, ObservedRefresh Refresh)>();
        while (_unmatched.TryDequeue(out var item))
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

        var stillUnmatched = (await AcknowledgeAsync(queued.Select(r => r.Refresh).ToList())).ToHashSet();
        foreach (var item in queued.Where(r => stillUnmatched.Contains(r.Refresh)))
        {
            _unmatched.Enqueue(item);
        }
    }

    public bool HasQueued => _unmatched.IsEmpty is false;

    private async Task<IReadOnlyCollection<ObservedRefresh>> AcknowledgeAsync(IReadOnlyCollection<ObservedRefresh> refreshes)
    {
        List<EventRow> unreleased;
        using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
        {
            unreleased = LedgerSql.Unreleased(scope.Database, 500);
        }

        var matched = new List<(Guid, PurgePlan)>();
        var used = new HashSet<ObservedRefresh>();
        foreach (EventRow row in unreleased)
        {
            if (_acknowledged.ContainsKey(row.Id) || row.Signal is null)
            {
                continue;
            }

            AckSignal? signal = JsonSerializer.Deserialize<AckSignal>(row.Signal, ChangeRecorder.Json);
            ObservedRefresh? refresh = signal is null ? null : refreshes.FirstOrDefault(signal.IsMetBy);
            if (refresh is not null && JsonSerializer.Deserialize<PurgePlan>(row.Plan, ChangeRecorder.Json) is { } plan)
            {
                used.Add(refresh);
                matched.Add((row.Id, plan));
            }
        }

        if (matched.Count > 0)
        {
            await EvictAndAckAsync(matched);
        }

        return refreshes.Where(refresh => used.Contains(refresh) is false).ToList();
    }

    private async Task EvictAndAckAsync(IReadOnlyCollection<(Guid Id, PurgePlan Plan)> changes)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IDeliveryApiOutputCacheManager? outputCache = scope.ServiceProvider.GetService<IDeliveryApiOutputCacheManager>();
        if (outputCache is not null)
        {
            ICacheTagStore store = scope.ServiceProvider.GetRequiredService<ICacheTagStore>();
            foreach ((Guid _, PurgePlan plan) in changes)
            {
                if (plan.Everything)
                {
                    await outputCache.EvictAllAsync();
                    continue;
                }

                // The package tags each output cache entry with its response key (unprefixed).
                foreach (var responseKey in await store.FindResponseKeysAsync(plan.Keys, plan.Cultures))
                {
                    await outputCache.EvictByTagAsync(responseKey);
                }
            }
        }

        await transport.AckAsync(identity.Name, changes.Select(c => c.Id).ToList(), time.GetUtcNow().UtcDateTime);
        foreach ((Guid id, PurgePlan _) in changes)
        {
            _acknowledged.TryAdd(id, 0);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Cache tags: {Server} acknowledged {Changes}.", identity.Name, string.Join(", ", changes.Select(c => c.Id)));
        }
    }

    /// <summary>Forgets acknowledged changes once released, so the set does not grow forever.</summary>
    public void Forget(Guid changeId) => _acknowledged.TryRemove(changeId, out _);
}
