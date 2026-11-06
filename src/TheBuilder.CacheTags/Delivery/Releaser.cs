using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The ack stage: releases each pending change once every server that must apply it has
///     acknowledged it (or the ack timeout passed), queueing its purge to every target. A change whose
///     listings wait for the servers' indexes moves on to <see cref="IndexStage" />. The purge reaches
///     the stored key sets that hold the change's items, looked up as it is queued and once more
///     <see cref="SetsRecheckDelay" /> later.
/// </summary>
internal sealed class Releaser(
    LedgerStore ledger,
    KeySetStore keySets,
    IAckTransport transport,
    ServerIdentity identity,
    PurgeTargets targets,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<Releaser> logger)
{
    /// <summary>How long a change waits for every server to apply it before it is released anyway.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How long after a release the change's key sets are looked up again. A render that read an
    ///     item just before a server applied the change can store its set just after the release looked
    ///     the sets up; the second look finds it. The window it covers is one render's duration.
    /// </summary>
    public static readonly TimeSpan SetsRecheckDelay = TimeSpan.FromSeconds(30);

    // How much earlier than the release a set may have been stored and still be looked up again: the
    // servers' clocks can be apart by this much (see AckProcessor.EarlySlack).
    private static readonly TimeSpan ClockSlack = AckProcessor.EarlySlack;

    /// <summary>Releases the changes that are ready. Returns how many are still waiting.</summary>
    public async Task<int> ReleaseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> pending = ledger.InStage(ChangeStage.Pending, 100);
        if (pending.Count == 0)
        {
            return 0;
        }

        CacheTagsOptions settings = options.CurrentValue;
        DateTime now = time.GetUtcNow().UtcDateTime;
        IReadOnlyCollection<ServerPresence> servers = identity.Among(await transport.GetServersAsync(cancellationToken), now);
        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = await transport.GetAcksAsync(pending.Select(change => change.Id).ToList(), cancellationToken);

        var waiting = 0;
        foreach (LedgerEntry change in pending)
        {
            // The recording server is not counted until its own refresh acknowledges the change:
            // Umbraco raises some changes (unpublish, delete, sort, media save) before the refresh.
            StageBarrier barrier = change.Signal is { WaitsForServers: false }
                ? StageBarrier.Opened
                : StageBarrier.ForAcks(change.CreatedUtc, now, servers, acks.AcknowledgedBy(change.Id), AckTimeout, Heartbeat.ServerTimeout);
            if (barrier.Open)
            {
                await ReleaseAsync(change, barrier, settings, now, cancellationToken);
            }
            else
            {
                waiting++;
            }
        }

        return waiting;
    }

    private async Task ReleaseAsync(LedgerEntry change, StageBarrier barrier, CacheTagsOptions settings, DateTime now, CancellationToken cancellationToken)
    {
        // A response showing too many items to list carries their set instead, so a change to one of
        // them purges every set that holds it.
        var items = Items(change.Plan);
        IReadOnlyList<string> sets = items.Count == 0 ? [] : keySets.SetsOf(items);
        IReadOnlyList<string> keys = [.. change.Plan.Purged(settings.KeyPrefix), .. sets.Select(set => CacheKeys.WithPrefix(settings.KeyPrefix, set))];
        IReadOnlyList<NewPurge> purges = NewPurge.ToTargets(targets.Current().Values, keys, now);

        // The listing selectors are in this purge too, so the items listings show are fresh at once;
        // their membership and order follow in the indexing stage.
        ChangeStage next = IndexCheck.WaitsFor(change.Change, change.Plan) ? ChangeStage.Indexing : ChangeStage.Released;
        DateTime? setsCheck = items.Count == 0 ? null : now + SetsRecheckDelay;
        if (ledger.Release(change.Id, next, barrier.Waiting, now, purges, setsCheck) is false)
        {
            return;
        }

        if (barrier.TimedOut)
        {
            logger.LogWarning(
                "Cache tags change {ChangeId} was sent after {Timeout} without acknowledgement from {Servers}. Those servers may still serve the old content.",
                change.Id,
                AckTimeout,
                string.Join(", ", barrier.Waiting));
        }

        logger.LogInformation("Cache tags change {ChangeId} released: {KeyCount} keys in {PurgeCount} purges.", change.Id, keys.Count, purges.Count);
    }

    /// <summary>
    ///     Looks up once more the key sets of the released changes whose second look is due, and
    ///     queues a purge of those stored since each change was released. Returns how many changes it
    ///     looked at.
    /// </summary>
    public int RecheckSets()
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        IReadOnlyList<LedgerEntry> due = ledger.SetsCheckDue(now, 100);
        if (due.Count == 0)
        {
            return 0;
        }

        CacheTagsOptions settings = options.CurrentValue;
        foreach (LedgerEntry change in due)
        {
            // Sets stored before the release were found then; these were stored since.
            var keys = keySets.SetsOf(Items(change.Plan), (change.ReleasedUtc ?? change.CreatedUtc) - ClockSlack)
                .Select(set => CacheKeys.WithPrefix(settings.KeyPrefix, set))
                .ToList();
            if (ledger.SetsChecked(change.Id, now, NewPurge.ToTargets(targets.Current().Values, keys, now)) && keys.Count > 0)
            {
                logger.LogInformation("Cache tags change {ChangeId}: {SetCount} key sets stored since its release purged.", change.Id, keys.Count);
            }
        }

        return due.Count;
    }

    // The items a plan purges, whose sets the purge reaches too. A plan of everything reaches every set.
    private static List<string> Items(PurgePlan plan)
        => plan.Everything ? [] : plan.Keys.Where(CacheKeys.IsItem).ToList();
}
