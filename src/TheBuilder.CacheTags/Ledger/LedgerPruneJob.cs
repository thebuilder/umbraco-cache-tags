using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Once a day, on one server: removes released changes and sent purges older than
///     <see cref="Retention" />, and key sets no server has rendered for <see cref="KeySetRetention" />.
/// </summary>
internal sealed class LedgerPruneJob(
    IAckTransport transport,
    LedgerStore ledger,
    KeySetStore keySets,
    ChangeRecorder recorder,
    PackageDatabase database,
    TimeProvider time) : IRecurringBackgroundJob
{
    /// <summary>How long released changes and sent purges are kept for the backoffice.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>
    ///     How long a key set is kept after a server last rendered a response carrying it. A response
    ///     cached for longer still carries it, so a pruned set is purged as it goes: such a response
    ///     renders again, and stores its set again.
    /// </summary>
    public static readonly TimeSpan KeySetRetention = TimeSpan.FromDays(90);

    // The most key sets pruned in one run, and so purged in one change.
    private const int KeySetsPerRun = 5000;

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

        DateTime cutoff = time.GetUtcNow().UtcDateTime - Retention;
        await transport.PruneAsync(cutoff);
        ledger.Prune(cutoff);
        PruneKeySets(time.GetUtcNow().UtcDateTime);
    }

    /// <summary>Removes the key sets unseen for <see cref="KeySetRetention" />, and purges them. Returns the change that purges them, if any.</summary>
    public Guid? PruneKeySets(DateTime nowUtc)
    {
        IReadOnlyList<string> pruned = keySets.Prune(nowUtc - KeySetRetention, KeySetsPerRun);
        if (pruned.Count == 0)
        {
            return null;
        }

        var change = new Change(Guid.CreateVersion7(), nowUtc, ChangeKind.Custom, [], [], null, $"{pruned.Count} unused key sets pruned", pruned);
        recorder.Record(change);
        return change.Id;
    }
}
