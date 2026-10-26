using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     What the lookup API answers. Public only because the controller's constructor takes it; its
///     members are internal, so nothing outside the package can implement or call it.
/// </summary>
public interface ICacheTagsChangeLog
{
    internal Task<IReadOnlyList<ChangeEntry>> ChangesAsync(int take, CancellationToken cancellationToken);
}

internal sealed record ChangeEntry(
    Guid Id,
    DateTime CreatedUtc,
    string Kind,
    string Server,
    string Status,
    bool WaitsForServers,
    DateTime? ReleasedUtc,
    IReadOnlyList<string> AcknowledgedBy,
    IReadOnlyList<string> NotAcknowledgedBy,
    IReadOnlyList<string> IndexedBy,
    Change Change,
    PurgePlan Plan,
    IReadOnlyList<Purge> Purges);

internal sealed class CacheTagsChangeLog(LedgerStore ledger, IAckTransport transport) : ICacheTagsChangeLog
{
    /// <summary>The latest changes, newest first: what each planned, who applied and indexed it, and its purges.</summary>
    public async Task<IReadOnlyList<ChangeEntry>> ChangesAsync(int take, CancellationToken cancellationToken)
    {
        IReadOnlyList<LedgerEntry> changes = ledger.Latest(Math.Clamp(take, 1, 200));
        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = await transport.GetAcksAsync(changes.Select(change => change.Id).ToList(), cancellationToken);
        return changes
            .Select(change => new ChangeEntry(
                change.Id,
                change.CreatedUtc,
                change.Change.Kind.ToString(),
                change.Server,
                change.Stage.ToString(),
                change.Signal is not { WaitsForServers: false },
                change.ReleasedUtc,
                acks.AcknowledgedBy(change.Id).Order(StringComparer.Ordinal).ToList(),
                change.NotAcknowledgedBy,
                acks.IndexedBy(change.Id).Order(StringComparer.Ordinal).ToList(),
                change.Change,
                change.Plan,
                ledger.PurgesOf(change.Id)))
            .ToList();
    }
}
