using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Turns a change into the keys to purge. The planned keys reach every response that showed the
///     changed items (item keys) and every listing they could have joined or left (selector keys).
///     Route changes reach the descendants too, whose routes follow (docs/findings.md, section 1).
/// </summary>
internal sealed class PurgePlanner(IDescendants descendants)
{
    public PurgePlan Plan(Change change, int descendantBudget)
    {
        if (change.Kind == ChangeKind.Everything)
        {
            return new PurgePlan([], Everything: true, Cultures: null);
        }

        if (change.Kind == ChangeKind.Custom)
        {
            // The keys as named; "umbraco" among them purges everything.
            var named = change.Keys ?? [];
            return named.Contains(CacheKeys.All)
                ? new PurgePlan([], Everything: true, Cultures: null)
                : new PurgePlan(named.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), Everything: false, change.Cultures);
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (change.Kind == ChangeKind.TypesChanged)
        {
            // Listings of the type, and every response that renders an item of it.
            keys.UnionWith(change.TypeAliases.Select(CacheKeys.Type));
            keys.UnionWith(change.TypeAliases.Select(CacheKeys.Schema));
            return new PurgePlan(keys, Everything: false, change.Cultures);
        }

        var media = change.Kind is ChangeKind.MediaSaved or ChangeKind.MediaMoved or ChangeKind.MediaTrashed or ChangeKind.MediaDeleted;
        var routeDescendants = new HashSet<Guid>();
        foreach (ChangedItem item in change.Items)
        {
            if (change.Kind == ChangeKind.Sorted)
            {
                // Only the order of the parent's children changed; the items themselves did not.
                keys.Add(CacheKeys.Children(item.ParentKey));
                continue;
            }

            keys.Add(media ? CacheKeys.Media(item.Key) : CacheKeys.Content(item.Key));
            keys.Add(CacheKeys.Children(item.ParentKey));
            if (item.OldParentKey is { } oldParent)
            {
                keys.Add(CacheKeys.Children(oldParent));
            }

            keys.Add(CacheKeys.Type(item.TypeAlias));
            keys.Add(CacheKeys.Search(media ? ListingSelectors.MediaSearch : ListingSelectors.ContentIndex));

            var routeChanged = item.RouteChanged || change.Kind is ChangeKind.Unpublished or ChangeKind.Moved or ChangeKind.Trashed or ChangeKind.Deleted;
            if (media is false && routeChanged)
            {
                routeDescendants.UnionWith(descendants.Of(item.Key));
                if (routeDescendants.Count > descendantBudget)
                {
                    return new PurgePlan([], Everything: true, change.Cultures);
                }
            }
        }

        keys.UnionWith(routeDescendants.Select(CacheKeys.Content));
        return new PurgePlan(keys, Everything: false, change.Cultures);
    }
}

/// <summary>A document's descendants, from Umbraco's in-memory navigation structure.</summary>
internal interface IDescendants
{
    IEnumerable<Guid> Of(Guid key);
}

internal sealed class NavigationDescendants(Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService navigation) : IDescendants
{
    // A trashed document's descendants moved to the recycle bin with it.
    public IEnumerable<Guid> Of(Guid key)
        => navigation.TryGetDescendantsKeys(key, out IEnumerable<Guid> keys) ? keys
            : navigation.TryGetDescendantsKeysInBin(key, out IEnumerable<Guid> binned) ? binned
            : [];
}
