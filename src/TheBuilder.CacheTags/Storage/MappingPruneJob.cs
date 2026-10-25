using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
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

        DateTime now = time.GetUtcNow().UtcDateTime;

        // Once every purge of a change was sent, no frontend keeps a copy of its responses rendered
        // before the change, so their rows are not needed until they are rendered again. Not while a
        // server released without the change can still evict its responses late by their keys. The
        // writer skips rewriting a render it wrote within its touch interval, so a response rendered
        // in the interval before the change may have been rendered again after it without its row
        // showing that: those rows stay until a later change or the retention removes them.
        var forgotten = 0;
        foreach (DeliveredChange change in ledger.Delivered(now - AckProcessor.LateWindow, options.CurrentValue.KeyPrefix))
        {
            forgotten += await store.ForgetAsync(change.ResponseKeys, change.CreatedUtc - MappingWriter.TouchInterval);
        }

        logger.LogInformation("Forgot {Count} cache tag mapping rows of responses that sent purges expired.", forgotten);

        DateTime mappingCutoff = now - options.CurrentValue.MappingRetention;
        var removed = await store.PruneAsync(mappingCutoff);
        logger.LogInformation("Pruned {Count} cache tag mapping rows older than {Cutoff:u}.", removed, mappingCutoff);

        DateTime ledgerCutoff = now - options.CurrentValue.LedgerRetention;
        await transport.PruneAsync(ledgerCutoff);
        ledger.Prune(ledgerCutoff);
    }
}
