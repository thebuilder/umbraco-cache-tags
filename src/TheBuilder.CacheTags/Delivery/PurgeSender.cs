using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>Sends the purges that are due as signed webhooks, under leases, retrying failures with backoff.</summary>
internal sealed class PurgeSender(
    LedgerStore ledger,
    IEnumerable<IPurgeTarget> senders,
    PurgeTargets targets,
    ServerIdentity identity,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<PurgeSender> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>Sends the purges that are due. Returns how many are still pending, now or later.</summary>
    public async Task<int> SendAsync(CancellationToken cancellationToken)
    {
        CacheTagsOptions settings = options.CurrentValue;
        (IReadOnlyList<Purge> due, var pending) = ledger.Due(time.GetUtcNow().UtcDateTime, 20);

        foreach (Purge purge in due)
        {
            // The time is read again for each claim: sending the purges before it can take longer than
            // the lease, and a lease from the start of the pass would be over before this one is sent.
            DateTime claimed = time.GetUtcNow().UtcDateTime;
            if (ledger.ClaimSend(purge.Id, identity.Name, claimed, claimed + Lease) is false)
            {
                continue;
            }

            if (Exists(purge.Target) is false)
            {
                // Queued from another server's list just before the target was deleted.
                ledger.Discard(purge.Id);
                logger.LogInformation("Cache tags purge {PurgeId} was set aside: no target named {Target} exists any more.", purge.Id, purge.Target);
                continue;
            }

            var error = await SendAsync(purge, settings, cancellationToken);
            DateTime finished = time.GetUtcNow().UtcDateTime;
            if (error is null)
            {
                ledger.Sent(purge.Id, identity.Name, finished);
                continue;
            }

            ledger.Failed(purge.Id, identity.Name, purge.Attempts + 1, settings.MaxAttempts, error, finished);
            logger.LogWarning("Cache tags purge {PurgeId} to {Target} failed (attempt {Attempt} of {Max}): {Error}", purge.Id, purge.Target, purge.Attempts + 1, settings.MaxAttempts, error);
        }

        return Math.Max(0, pending - due.Count);
    }

    // The targets this server knows are up to a few seconds old, so a miss is read again before it counts.
    private bool Exists(string target)
    {
        if (targets.Current().ContainsKey(target))
        {
            return true;
        }

        targets.Invalidate();
        return targets.Current().ContainsKey(target);
    }

    /// <summary>Sends one purge through its target's type. Returns null on success, or what went wrong.</summary>
    private async Task<string?> SendAsync(Purge purge, CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        if (targets.Current().TryGetValue(purge.Target, out ResolvedTarget? resolved) is false || resolved.Options.Enabled is false)
        {
            return $"No enabled target named {purge.Target} is configured.";
        }

        if (resolved.Problem is not null)
        {
            return resolved.Problem;
        }

        CacheTagsTargetOptions target = resolved.Options;
        IPurgeTarget? sender = senders.FirstOrDefault(t => string.Equals(t.Type, target.Type, StringComparison.OrdinalIgnoreCase));
        if (sender is null)
        {
            return $"No purge target of type {target.Type} is registered.";
        }

        var request = new PurgeRequest
        {
            Id = purge.Id,
            ChangeId = purge.ChangeId,
            TargetName = purge.Target,
            Expire = purge.Keys.Where(key => CacheKeys.Expires(key, settings.KeyPrefix)).ToList(),
            Stale = purge.Keys.Where(key => CacheKeys.Expires(key, settings.KeyPrefix) is false).ToList(),
        };

        try
        {
            return await sender.SendAsync(request, target, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested is false)
        {
            return $"{target.Type} target failed: {exception.Message}";
        }
    }
}
