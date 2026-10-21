using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>Sends the purges that are due as signed webhooks, under leases, retrying failures with backoff.</summary>
internal sealed class PurgeSender(
    LedgerStore ledger,
    OutputCacheEviction eviction,
    IHttpClientFactory httpClientFactory,
    ServerIdentity identity,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<PurgeSender> logger)
{
    public const string HttpClientName = "TheBuilder.CacheTags";

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

    /// <summary>Posts one signed purge. Returns null on success, or what went wrong.</summary>
    private async Task<string?> SendAsync(Purge purge, CacheTagsOptions settings, CancellationToken cancellationToken)
    {
        CacheTagsTargetOptions? target = settings.Targets.FirstOrDefault(t => t.Name == purge.Target && t.Enabled);
        if (target is null)
        {
            return $"No enabled target named {purge.Target} is configured.";
        }

        // The acks evicted the first purge's responses from every server's output cache; a listing
        // re-purge comes later, so this evicts them on the sending server before the target refetches.
        var responsePrefix = settings.KeyPrefix + CacheKeys.ResponsePrefix;
        await eviction.EvictAsync(purge.Keys.Where(key => key.StartsWith(responsePrefix, StringComparison.Ordinal)).Select(key => key[settings.KeyPrefix.Length..]));

        using HttpRequestMessage request = Webhook.Request(new Uri(target.Url), target.Secret, purge.Id, purge.Keys, time.GetUtcNow().ToUnixTimeSeconds());
        try
        {
            using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? null
                : $"{(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return exception.Message;
        }
    }
}
