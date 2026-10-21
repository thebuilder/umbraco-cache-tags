using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Removes mapping rows older than <see cref="CacheTagsOptions.MappingRetention" />, once a day, on
///     one server.
/// </summary>
internal sealed class MappingPruneJob(
    ICacheTagStore store,
    IAckTransport transport,
    LedgerStore ledger,
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
        DateTime now = time.GetUtcNow().UtcDateTime;
        DateTime mappingCutoff = now - options.CurrentValue.MappingRetention;
        var removed = await store.PruneAsync(mappingCutoff);
        logger.LogInformation("Pruned {Count} cache tag mapping rows older than {Cutoff:u}.", removed, mappingCutoff);

        DateTime ledgerCutoff = now - options.CurrentValue.LedgerRetention;
        await transport.PruneAsync(ledgerCutoff);
        ledger.Prune(ledgerCutoff);
    }
}
