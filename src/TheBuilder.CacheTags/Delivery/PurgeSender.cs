using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>Sends the purges that are due as signed webhooks, under leases, retrying failures with backoff.</summary>
internal sealed class PurgeSender(
    LedgerStore ledger,
    IEnumerable<IPurgeTarget> targets,
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
        DateTime now = time.GetUtcNow().UtcDateTime;
        (IReadOnlyList<Purge> due, var pending) = ledger.Due(now, 20);

        foreach (Purge purge in due.Where(purge => ledger.ClaimSend(purge.Id, identity.Name, now, now + Lease)))
        {
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

    /// <summary>Sends one purge through its target's type. Returns null on success, or what went wrong.</summary>
    private async Task<string?> SendAsync(Purge purge, CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        if (settings.Targets.TryGetValue(purge.Target, out CacheTagsTargetOptions? target) is false || target.Enabled is false)
        {
            return $"No enabled target named {purge.Target} is configured.";
        }

        IPurgeTarget? sender = targets.FirstOrDefault(t => string.Equals(t.Type, target.Type, StringComparison.OrdinalIgnoreCase));
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
