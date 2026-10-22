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
    ///     Whether a change changes what content listings return: a change to content items, or a
    ///     content type's rename, which moves its documents to another type filter. Media listings walk
    ///     the media cache, not an index; other type and site-wide changes are purged as a whole at once.
    /// </summary>
    public static bool Waits(Change change)
        => change.Kind is ChangeKind.Published or ChangeKind.Unpublished or ChangeKind.Moved or ChangeKind.Trashed or ChangeKind.Deleted or ChangeKind.Sorted
            || (change.Kind is ChangeKind.TypesChanged && change.RenamedAliases is { Count: > 0 });

    /// <summary>Whether a change's listing purge waits for every server's index: it changes content listings, and its plan reaches some.</summary>
    public static bool WaitsFor(Change change, PurgePlan plan)
        => Waits(change) && plan.Everything is false && plan.Keys.Any(Keys.CacheKeys.IsSelector);

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
