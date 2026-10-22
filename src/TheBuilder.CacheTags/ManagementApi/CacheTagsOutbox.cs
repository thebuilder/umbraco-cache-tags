using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>The purge outbox, for the Management API: what failed, and sending it again or setting it aside.</summary>
public interface ICacheTagsOutbox
{
    internal IReadOnlyList<Purge> Purges(string state, int take);

    internal bool Retry(Guid purgeId);

    internal bool Discard(Guid purgeId);
}

internal sealed class CacheTagsOutbox(LedgerStore ledger, DeliverySignal signal, TimeProvider time) : ICacheTagsOutbox
{
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        OutboxStates.Pending, OutboxStates.Sent, OutboxStates.Dead, OutboxStates.Discarded,
    };

    public IReadOnlyList<Purge> Purges(string state, int take) => ledger.InState(state, Math.Clamp(take, 1, 200));

    public bool Retry(Guid purgeId)
    {
        var retried = ledger.Retry(purgeId, time.GetUtcNow().UtcDateTime);
        if (retried)
        {
            signal.Wake();
        }

        return retried;
    }

    public bool Discard(Guid purgeId) => ledger.Discard(purgeId);
}
