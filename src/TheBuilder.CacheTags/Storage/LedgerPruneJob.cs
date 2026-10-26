using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace TheBuilder.CacheTags.Storage;

/// <summary>Removes released changes and sent purges older than <see cref="CacheTagsOptions.LedgerRetention" />, once a day, on one server.</summary>
internal sealed class LedgerPruneJob(
    IAckTransport transport,
    LedgerStore ledger,
    PackageDatabase database,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time) : IRecurringBackgroundJob
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

        DateTime cutoff = time.GetUtcNow().UtcDateTime - options.CurrentValue.LedgerRetention;
        await transport.PruneAsync(cutoff);
        ledger.Prune(cutoff);
    }
}
