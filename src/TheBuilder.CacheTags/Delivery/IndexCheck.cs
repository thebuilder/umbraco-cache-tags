using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>One document of an item in this server's Delivery API index (one per culture).</summary>
/// <param name="Culture">The document's culture, or <c>none</c>.</param>
/// <param name="State">The item's state when the document was built, or <c>null</c> for one built before the package was installed.</param>
internal sealed record IndexedDocument(string Culture, string? State);

/// <summary>
///     Whether this server's Delivery API index has caught up with a change. Listings come from the
///     index, so their purge waits for this on every server; the items themselves come from the
///     published cache, which the ack already covers.
/// </summary>
internal static class IndexCheck
{
    /// <summary>
    ///     Whether a change of this kind changes what content listings return. Media listings walk the
    ///     media cache, not an index; type and site-wide changes are purged as a whole at once.
    /// </summary>
    public static bool Waits(ChangeKind kind)
        => kind is ChangeKind.Published or ChangeKind.Unpublished or ChangeKind.Moved or ChangeKind.Trashed or ChangeKind.Deleted or ChangeKind.Sorted;

    /// <summary>Whether a change's listing purge waits for every server's index: it changes content listings, and its plan reaches some.</summary>
    public static bool WaitsFor(ChangeKind kind, PurgePlan plan)
        => Waits(kind) && plan.Everything is false && plan.Keys.Any(Keys.CacheKeys.IsSelector);

    /// <summary>
    ///     Whether the index has an item as it is in the database now, and so every change made to it
    ///     so far: every document of it was built from that state. With no <paramref name="state" />
    ///     (the item is gone, trashed, or of a type the index skips), whether no document is left.
    /// </summary>
    public static bool HasIndexed(string? state, IReadOnlyCollection<IndexedDocument> documents)
        => state is null
            ? documents.Count == 0
            : documents.Count > 0 && documents.All(document => document.State == state);
}

/// <summary>Whether a released change's listings may be purged yet, and which servers they would be purged without.</summary>
internal sealed record ListingDecision(bool Release, bool TimedOut, IReadOnlyList<string> Waiting)
{
    /// <summary>
    ///     A change's listings are purged once every active server that acknowledged the change has
    ///     indexed it, or once <paramref name="indexTimeout" /> has passed since the change was
    ///     released. A server that stopped (no heartbeat within <paramref name="serverTimeout" />)
    ///     serves nothing, so nothing waits for it.
    /// </summary>
    public static ListingDecision Decide(
        DateTime releasedUtc,
        DateTime nowUtc,
        IEnumerable<ServerPresence> servers,
        IReadOnlySet<string> acknowledgedBy,
        IReadOnlySet<string> indexedBy,
        TimeSpan indexTimeout,
        TimeSpan serverTimeout)
    {
        var active = servers.Where(server => server.LastSeenUtc >= nowUtc - serverTimeout).Select(server => server.Server).ToHashSet(StringComparer.Ordinal);
        var waiting = acknowledgedBy
            .Where(server => active.Contains(server) && indexedBy.Contains(server) is false)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (waiting.Count == 0)
        {
            return new ListingDecision(true, false, []);
        }

        var timedOut = nowUtc - releasedUtc >= indexTimeout;
        return new ListingDecision(timedOut, timedOut, waiting);
    }
}
