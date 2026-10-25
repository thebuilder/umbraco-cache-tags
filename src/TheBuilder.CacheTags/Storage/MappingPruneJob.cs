using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Once a day, on one server: forgets the responses that sent purges have expired, removes mapping
///     rows older than <see cref="CacheTagsOptions.MappingRetention" />, and prunes the ledger.
/// </summary>
internal sealed class MappingPruneJob(
    ICacheTagStore store,
    IAckTransport transport,
    LedgerStore ledger,
    PackageDatabase database,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<MappingPruneJob> logger) : IRecurringBackgroundJob
{
    public TimeSpan Period => TimeSpan.FromDays(1);

    public TimeSpan Delay => TimeSpan.FromMinutes(5);

    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public event EventHandler PeriodChanged { add { } remove { } }

    public async Task RunJobAsync()
    {
        if (database.IsInstalled() is false)
        {
            return;
        }

        CacheTagsOptions settings = options.CurrentValue;
        DateTime now = time.GetUtcNow().UtcDateTime;
        await ForgetAsync(settings.KeyPrefix, now);

        // The writer touches a response at most once per touch interval, so its seen time can be that
        // much behind the last request.
        DateTime mappingCutoff = now - settings.MappingRetention - MappingWriter.TouchInterval;
        var removed = await store.PruneAsync(mappingCutoff);
        logger.LogInformation("Pruned {Count} cache tag mapping rows older than {Cutoff:u}.", removed, mappingCutoff);

        DateTime ledgerCutoff = now - settings.LedgerRetention;
        await transport.PruneAsync(ledgerCutoff);
        ledger.Prune(ledgerCutoff);
    }

    /// <summary>
    ///     Forgets the responses a sent purge expired: no frontend keeps a copy from before the change
    ///     (docs/design.md). Not before a server released without the change has stopped evicting late
    ///     (<see cref="AckProcessor.LateWindow" />), and only rows a writer's touch interval older than
    ///     the change (<see cref="MappingWriter.TouchInterval" />). Changes released in the last two
    ///     runs, so one missed run is still covered. Forgetting only saves rows the retention would
    ///     remove anyway, so a failure is logged and the job goes on.
    /// </summary>
    private async Task ForgetAsync(string prefix, DateTime now)
    {
        try
        {
            var forgotten = 0;
            foreach (DeliveredChange change in ledger.Delivered(now - 2 * Period, now - AckProcessor.LateWindow))
            {
                var responseKeys = change.Keys
                    .Select(key => CacheKeys.WithoutPrefix(prefix, key))
                    .Where(CacheKeys.IsResponse)
                    .ToList();
                if (responseKeys.Count > 0)
                {
                    forgotten += await store.ForgetAsync(responseKeys, change.CreatedUtc - MappingWriter.TouchInterval);
                }
            }

            logger.LogInformation("Forgot {Count} cache tag mapping rows of responses that sent purges expired.", forgotten);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Forgetting cache tag mapping rows that sent purges expired failed.");
        }
    }
}
