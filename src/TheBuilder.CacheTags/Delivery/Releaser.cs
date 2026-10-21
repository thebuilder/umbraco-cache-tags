using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Releases changes once every server that must apply them has acknowledged them (or the ack
///     timeout passed), queueing one purge per target and the listing re-purges.
/// </summary>
internal sealed class Releaser(
    LedgerStore ledger,
    PurgeResolver resolver,
    AckProcessor acks,
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
            // The server that recorded the change applied its refresh first, so it has acknowledged it.
            var acknowledgedBy = new HashSet<string>(StringComparer.Ordinal) { change.Server };
            acknowledgedBy.UnionWith(acked.GetValueOrDefault(change.Id) ?? new HashSet<string>());

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
        IReadOnlyList<string> listingKeys = resolved.ListingKeys(settings.KeyPrefix);

        // The purge now, then the listings again as the Delivery API index catches up with the change.
        var sends = new List<(IReadOnlyList<string> Keys, DateTime NotBeforeUtc)> { (keys, now) };
        if (listingKeys.Count > 0)
        {
            sends.AddRange(settings.ListingRepurgeDelays.Where(delay => delay > TimeSpan.Zero).Distinct().Select(delay => (listingKeys, now + delay)));
        }

        var purges = settings.Targets
            .Where(target => target.Value.Enabled)
            .SelectMany(target => sends.SelectMany(send => send.Keys.Chunk(target.Value.MaxKeysPerRequest)
                .Select(chunk => new NewPurge(target.Key, chunk, send.NotBeforeUtc))))
            .ToList();

        if (ledger.CompleteRelease(change.Id, identity.Name, decision.Missing, decision.TimedOut, now, purges) is false)
        {
            return;
        }

        acks.Forget(change.Id);
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
}
