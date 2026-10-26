using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The ack stage: releases each pending change once every server that must apply it has
///     acknowledged it (or the ack timeout passed), queueing its purge to every target. A change whose
///     listings wait for the servers' indexes moves on to <see cref="IndexStage" />.
/// </summary>
internal sealed class Releaser(
    LedgerStore ledger,
    IAckTransport transport,
    ServerIdentity identity,
    PurgeTargets targets,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<Releaser> logger)
{
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
                : StageBarrier.ForAcks(change.CreatedUtc, now, servers, acks.AcknowledgedBy(change.Id), settings.AckTimeout, settings.ServerTimeout);
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
        IReadOnlyList<string> keys = change.Plan.Purged(settings.KeyPrefix);
        IReadOnlyList<NewPurge> purges = NewPurge.ToTargets(targets.Current().Values, keys, now);

        // The listing selectors are in this purge too, so the items listings show are fresh at once;
        // their membership and order follow in the indexing stage.
        ChangeStage next = IndexCheck.WaitsFor(change.Change, change.Plan) ? ChangeStage.Indexing : ChangeStage.Released;
        if (ledger.Release(change.Id, next, barrier.Waiting, now, purges) is false)
        {
            return;
        }

        if (barrier.TimedOut)
        {
            logger.LogWarning(
                "Cache tags change {ChangeId} was sent after {Timeout} without acknowledgement from {Servers}. Those servers may still serve the old content.",
                change.Id,
                settings.AckTimeout,
                string.Join(", ", barrier.Waiting));
        }

        logger.LogInformation("Cache tags change {ChangeId} released: {KeyCount} keys in {PurgeCount} purges.", change.Id, keys.Count, purges.Count);
    }
}
