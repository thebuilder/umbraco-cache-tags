using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>One document of an item in this server's Delivery API index (one per culture).</summary>
/// <param name="Culture">The document's culture, or <c>none</c>.</param>
/// <param name="BuiltUtc">When the package's index handler saw it built, or <c>null</c> for a document built before the package was installed.</param>
internal sealed record IndexedDocument(string Culture, DateTime? BuiltUtc);

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
    ///     Whether the index has an item as it is after the change: every document of it was built
    ///     since <paramref name="sinceUtc" /> (when this server applied the change), or, for a change
    ///     that takes it out of the index (a delete, or trashing), none is left. An item whose type the
    ///     index skips never has a document, so nothing waits for it.
    /// </summary>
    public static bool HasIndexed(ChangeKind kind, IReadOnlyCollection<IndexedDocument> documents, DateTime sinceUtc, bool indexable)
    {
        if (indexable is false)
        {
            return true;
        }

        if (documents.Count == 0)
        {
            return kind is ChangeKind.Deleted or ChangeKind.Trashed;
        }

        return documents.All(document => document.BuiltUtc >= sinceUtc);
    }
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
