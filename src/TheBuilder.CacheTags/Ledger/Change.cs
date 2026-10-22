namespace TheBuilder.CacheTags.Ledger;

/// <summary>What happened, on the server where it happened.</summary>
internal enum ChangeKind
{
    Published,
    Unpublished,
    Moved,
    Trashed,
    Deleted,
    Sorted,
    MediaSaved,
    MediaMoved,
    MediaTrashed,
    MediaDeleted,
    TypesChanged,
    Everything,

    /// <summary>Keys a site or a person named (<c>ICacheTagsChanges</c>): custom output, or a manual purge.</summary>
    Custom,
}

/// <summary>Where a recorded change is on its way to the frontends.</summary>
internal enum ChangeStage
{
    /// <summary>Waiting for every server to apply it (the ack barrier).</summary>
    Pending,

    /// <summary>Its purge is queued; its listing purge waits for every server's Delivery API index.</summary>
    Indexing,

    /// <summary>Every purge for it is queued.</summary>
    Released,
}

/// <summary>
///     One change to published output, recorded by the server that made it. Draft-only saves never
///     become a change.
/// </summary>
/// <param name="Id">The event id, also sent with the purge so a frontend can ignore a repeat.</param>
/// <param name="CreatedUtc">When it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Items">The documents or media affected.</param>
/// <param name="TypeAliases">For <see cref="ChangeKind.TypesChanged" />: the content and media types whose output changed.</param>
/// <param name="Cultures">The cultures that changed, or <c>null</c> for all.</param>
/// <param name="Reason">Why, for the log and the lookup API (for example "language da-DK saved").</param>
/// <param name="Keys">For <see cref="ChangeKind.Custom" />: the keys to purge.</param>
/// <param name="WaitFor">
///     For <see cref="ChangeKind.Custom" />: the signal every server reports once it has applied the
///     change (<c>ICacheTagsChanges.Applied</c>), or <c>null</c> to purge at once.
/// </param>
/// <param name="Sources">
///     For <see cref="ChangeKind.TypesChanged" /> and <see cref="ChangeKind.Everything" />: the cache
///     refreshes Umbraco sends for it (<c>language:5</c>, see <c>AckSignal.Source</c>), which are the
///     only ones that acknowledge it.
/// </param>
internal sealed record Change(
    Guid Id,
    DateTime CreatedUtc,
    ChangeKind Kind,
    IReadOnlyList<ChangedItem> Items,
    IReadOnlyList<string> TypeAliases,
    IReadOnlyList<string>? Cultures,
    string? Reason = null,
    IReadOnlyList<string>? Keys = null,
    string? WaitFor = null,
    IReadOnlyList<string>? Sources = null);

/// <param name="Key">The document or media key.</param>
/// <param name="TypeAlias">Its content or media type alias.</param>
/// <param name="ParentKey">Its parent's key; <see cref="Guid.Empty" /> at the root.</param>
/// <param name="OldParentKey">For moves: the parent it left.</param>
/// <param name="RouteChanged">
///     Its route changed in a way its descendants' routes follow: a new URL segment, a move, an
///     unpublish.
/// </param>
/// <param name="Ancestors">Its ancestors' keys, root first: a refresh of an ancestor's branch also refreshes it.</param>
internal sealed record ChangedItem(Guid Key, string TypeAlias, Guid ParentKey, Guid? OldParentKey = null, bool RouteChanged = false, IReadOnlyList<Guid>? Ancestors = null);

/// <summary>
///     The keys a change purges, before they are resolved to <c>response-</c> keys.
/// </summary>
/// <param name="Keys">Item keys (<c>content-</c>, <c>media-</c>) and selector keys (<c>type-</c>, <c>children-</c>, <c>search-</c>).</param>
/// <param name="Everything">Purge every response (<c>umbraco</c>), because the change is too wide to list.</param>
/// <param name="Cultures">The cultures to limit the purge to, or <c>null</c> for all.</param>
internal sealed record PurgePlan(IReadOnlyCollection<string> Keys, bool Everything, IReadOnlyList<string>? Cultures);
