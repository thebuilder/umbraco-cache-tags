using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Releases changes once every server that must apply them has acknowledged them (or the ack
///     timeout passed), queueing one purge per target. A change's listings are purged a second time
///     once every server that acknowledged it has it in its Delivery API index (or the index timeout
///     passed): listings come from the index, which Umbraco updates after the refresh.
/// </summary>
internal sealed class Releaser(
    LedgerStore ledger,
    PurgeResolver resolver,
    IAckTransport transport,
    ServerIdentity identity,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<Releaser> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>Releases the changes that are ready. Returns how many are still waiting.</summary>
    public async Task<int> ReleaseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> unreleased = ledger.Unreleased(100);
        if (unreleased.Count == 0)
        {
            return 0;
        }

        CacheTagsOptions settings = options.CurrentValue;
        IReadOnlyCollection<ServerPresence> servers = await transport.GetServersAsync(cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> acked = await transport.GetAcksAsync(unreleased.Select(change => change.Id).ToList(), cancellationToken);
        DateTime now = time.GetUtcNow().UtcDateTime;

        var waiting = 0;
        foreach (LedgerEntry change in unreleased)
        {
            // The recording server is not counted until its own refresh acknowledges the change:
            // Umbraco raises some changes (unpublish, delete, sort, media save) before the refresh.
            IReadOnlySet<string> acknowledgedBy = acked.GetValueOrDefault(change.Id) ?? new HashSet<string>();

            ReleaseDecision decision = ReleaseDecision.Decide(change.CreatedUtc, now, servers, acknowledgedBy, settings.AckTimeout, settings.ServerTimeout);
            if (decision.Release)
            {
                await ReleaseAsync(change, decision, settings, now, cancellationToken);
            }
            else
            {
                waiting++;
            }
        }

        return waiting;
    }

    private async Task ReleaseAsync(LedgerEntry change, ReleaseDecision decision, CacheTagsOptions settings, DateTime now, CancellationToken cancellationToken)
    {
        if (ledger.ClaimRelease(change.Id, identity.Name, now, now + Lease) is false)
        {
            return;
        }

        ResolvedPurge resolved = await resolver.ResolveAsync(change.Plan, cancellationToken);
        IReadOnlyList<string> keys = resolved.Keys(settings.KeyPrefix);

        // The listings are purged with everything else now, so the items they show are fresh at once,
        // and again once every server's index has the change, so their membership and order are.
        var listingsWait = resolved.Listings.Count > 0 && IndexCheck.WaitsFor(Enum.Parse<ChangeKind>(change.Kind), change.Plan);
        List<NewPurge> purges = Purges(settings, keys, now);
        if (ledger.CompleteRelease(change.Id, identity.Name, decision.Missing, decision.TimedOut, listingsWait, now, purges) is false)
        {
            return;
        }

        if (decision.TimedOut)
        {
            logger.LogWarning(
                "Cache tags change {ChangeId} was sent after {Timeout} without acknowledgement from {Servers}. Those servers may still serve the old content.",
                change.Id,
                settings.AckTimeout,
                string.Join(", ", decision.Missing));
        }

        logger.LogInformation("Cache tags change {ChangeId} released: {KeyCount} keys in {PurgeCount} purges.", change.Id, keys.Count, purges.Count);
    }

    /// <summary>Queues the listing purges of the released changes every server's index now has. Returns how many still wait.</summary>
    public async Task<int> ReleaseListingsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> waiting = ledger.ListingsWaiting(100);
        if (waiting.Count == 0)
        {
            return 0;
        }

        CacheTagsOptions settings = options.CurrentValue;
        IReadOnlyCollection<ServerPresence> servers = await transport.GetServersAsync(cancellationToken);
        var ids = waiting.Select(change => change.Id).ToList();
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> acked = await transport.GetAcksAsync(ids, cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> indexed = await transport.GetIndexedAsync(ids, cancellationToken);
        DateTime now = time.GetUtcNow().UtcDateTime;

        var stillWaiting = 0;
        foreach (LedgerEntry change in waiting)
        {
            ListingDecision decision = ListingDecision.Decide(
                change.ReleasedUtc ?? change.CreatedUtc,
                now,
                servers,
                acked.GetValueOrDefault(change.Id) ?? new HashSet<string>(),
                indexed.GetValueOrDefault(change.Id) ?? new HashSet<string>(),
                settings.IndexTimeout,
                settings.ServerTimeout);
            if (decision.Release is false)
            {
                stillWaiting++;
                continue;
            }

            // Resolved now, not at release: a listing first requested in between has a new response key.
            ResolvedPurge resolved = await resolver.ResolveAsync(change.Plan, cancellationToken);
            IReadOnlyList<string> keys = resolved.ListingKeys(settings.KeyPrefix);
            if (ledger.CompleteListings(change.Id, now, Purges(settings, keys, now)) is false)
            {
                continue;
            }

            if (decision.TimedOut)
            {
                logger.LogWarning(
                    "Cache tags change {ChangeId}'s listings were purged after {Timeout} without the Delivery API index of {Servers}. Their listings may still be old.",
                    change.Id,
                    settings.IndexTimeout,
                    string.Join(", ", decision.Waiting));
            }

            logger.LogInformation("Cache tags change {ChangeId} indexed: {KeyCount} listing keys purged.", change.Id, keys.Count);
        }

        return stillWaiting;
    }

    private static List<NewPurge> Purges(CacheTagsOptions settings, IReadOnlyList<string> keys, DateTime now)
        => keys.Count == 0
            ? []
            : settings.Targets
                .Where(target => target.Value.Enabled)
                .SelectMany(target => keys.Chunk(target.Value.MaxKeysPerRequest).Select(chunk => new NewPurge(target.Key, chunk, now)))
                .ToList();
}
