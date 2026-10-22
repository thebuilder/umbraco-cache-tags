using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The indexing stage of a released change. Listings come from each server's Delivery API index,
///     which Umbraco updates after the refresh, so each server checks its own index for the changes it
///     acknowledged, evicts their listing responses from its output cache once it has them, and
///     records them as indexed. Once every server that acknowledged a change has (or the index timeout
///     passed), one server queues its listing purge.
/// </summary>
internal sealed class IndexStage(
    LedgerStore ledger,
    IAckTransport transport,
    LocalDeliveryIndex index,
    PurgeResolver resolver,
    OutputCacheEviction eviction,
    ServerIdentity identity,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<IndexStage> logger)
{
    /// <summary>Records what this server's index has, and releases the listings that are ready. Returns how many still wait.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> indexing = ledger.InStage(ChangeStage.Indexing, 100);
        if (indexing.Count == 0)
        {
            return 0;
        }

        CacheTagsOptions settings = options.CurrentValue;
        DateTime now = time.GetUtcNow().UtcDateTime;
        IReadOnlyCollection<ServerPresence> servers = identity.Among(await transport.GetServersAsync(cancellationToken), now);
        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = await transport.GetAcksAsync(indexing.Select(change => change.Id).ToList(), cancellationToken);
        IReadOnlySet<Guid> indexedHere = await IndexHereAsync(indexing, acks, cancellationToken);

        var waiting = 0;
        foreach (LedgerEntry change in indexing)
        {
            var indexedBy = new HashSet<string>(acks.IndexedBy(change.Id), StringComparer.Ordinal);
            if (indexedHere.Contains(change.Id))
            {
                indexedBy.Add(identity.Name);
            }

            StageBarrier barrier = StageBarrier.ForIndex(change.ReleasedUtc ?? change.CreatedUtc, now, servers, acks.AcknowledgedBy(change.Id), indexedBy, settings.IndexTimeout, settings.ServerTimeout);
            if (barrier.Open is false)
            {
                waiting++;
                continue;
            }

            // Resolved now, not at release: a listing first requested in between has a new response key.
            IReadOnlyList<string> keys = (await resolver.ResolveAsync(change.Plan, cancellationToken)).ListingKeys(settings.KeyPrefix);
            if (ledger.ReleaseListings(change.Id, now, NewPurge.ToTargets(settings.Targets, keys, now)) is false)
            {
                continue;
            }

            if (barrier.TimedOut)
            {
                logger.LogWarning(
                    "Cache tags change {ChangeId}'s listings were purged after {Timeout} without the Delivery API index of {Servers}. Their listings may still be old.",
                    change.Id,
                    settings.IndexTimeout,
                    string.Join(", ", barrier.Waiting));
            }

            logger.LogInformation("Cache tags change {ChangeId} indexed: {KeyCount} listing keys purged.", change.Id, keys.Count);
        }

        return waiting;
    }

    /// <summary>The changes this server acknowledged that its index now has: their listing responses are evicted here, and they are recorded as indexed.</summary>
    private async Task<IReadOnlySet<Guid>> IndexHereAsync(IReadOnlyList<LedgerEntry> indexing, IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks, CancellationToken cancellationToken)
    {
        var indexed = indexing
            .Where(change => acks.AcknowledgedBy(change.Id).Contains(identity.Name) && acks.IndexedBy(change.Id).Contains(identity.Name) is false)
            .Where(change => index.Has(change.Change))
            .ToList();
        if (indexed.Count == 0)
        {
            return new HashSet<Guid>();
        }

        if (eviction.Enabled)
        {
            foreach (LedgerEntry change in indexed)
            {
                await eviction.EvictAsync((await resolver.ResolveAsync(change.Plan, cancellationToken)).Listings);
            }
        }

        await transport.IndexedAsync(identity.Name, indexed.Select(change => change.Id).ToList(), time.GetUtcNow().UtcDateTime, cancellationToken);
        return indexed.Select(change => change.Id).ToHashSet();
    }
}
