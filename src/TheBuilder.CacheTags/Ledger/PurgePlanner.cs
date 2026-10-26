using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Turns a change into the keys to purge. The planned keys reach every response that showed the
///     changed items (item keys) and every listing they could have joined or left (selector keys).
///     Route changes reach the descendants too, whose routes follow, and an unpublish also the listings
///     of their types, which they leave with the item.
/// </summary>
internal sealed class PurgePlanner(IDescendants descendants)
{
    public PurgePlan Plan(Change change, int descendantBudget)
    {
        if (change.Kind == ChangeKind.Everything)
        {
            return new PurgePlan([], Everything: true);
        }

        if (change.Kind == ChangeKind.Custom)
        {
            // The keys as named; "umbraco" among them purges everything.
            var named = change.Keys ?? [];
            return named.Contains(CacheKeys.All)
                ? new PurgePlan([], Everything: true)
                : new PurgePlan(named.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), Everything: false);
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (change.Kind == ChangeKind.TypesChanged)
        {
            // Any response may render an item of the type, so everything. Its listings are named too, so
            // they are purged again once every index has a renamed type (IndexCheck).
            keys.UnionWith(change.TypeAliases.Select(CacheKeys.Type));
            return new PurgePlan(keys, Everything: true);
        }

        var media = change.Kind is ChangeKind.MediaSaved or ChangeKind.MediaMoved or ChangeKind.MediaTrashed or ChangeKind.MediaDeleted;
        var routeDescendants = new HashSet<Guid>();
        foreach (ChangedItem item in change.Items)
        {
            if (change.Kind == ChangeKind.Sorted)
            {
                // The items did not change, only their order: in the parent's children, and in every
                // listing ordered by sort order that shows them (a type listing, or any other query).
                keys.Add(CacheKeys.Children(item.ParentKey));
                keys.Add(CacheKeys.Type(item.TypeAlias));
                keys.Add(CacheKeys.Search(ListingSelectors.ContentIndex));
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
                var below = descendants.Of(item.Key).ToList();
                routeDescendants.UnionWith(below);
                if (routeDescendants.Count > descendantBudget)
                {
                    return new PurgePlan([], Everything: true);
                }
            }
        }

        // The descendants' types too: a response that lists types instead of its items (past
        // MaxItemKeys) reaches them only that way, and an unpublish takes them out of their type
        // listings, including pages of them that do not show them.
        keys.UnionWith(routeDescendants.Select(CacheKeys.Content));
        if (routeDescendants.Count > 0)
        {
            try
            {
                keys.UnionWith(descendants.TypesOf(routeDescendants).Select(CacheKeys.Type));
            }
            catch (Exception)
            {
                // Recording must not fail with it: without their types, every listing is purged.
                return new PurgePlan([], Everything: true);
            }
        }

        return new PurgePlan(keys, Everything: false);
    }
}

/// <summary>A document's descendants, from Umbraco's in-memory navigation structure.</summary>
internal interface IDescendants
{
    IEnumerable<Guid> Of(Guid key);

    /// <summary>The content type aliases of the given documents.</summary>
    IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys);
}

internal sealed class NavigationDescendants(
    Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService navigation,
    Umbraco.Cms.Core.Services.IEntityService entities) : IDescendants
{
    // Below SQL Server's 2100 parameters per query.
    private const int TypesBatch = 1000;

    // A trashed document's descendants moved to the recycle bin with it.
    public IEnumerable<Guid> Of(Guid key)
        => navigation.TryGetDescendantsKeys(key, out IEnumerable<Guid> keys) ? keys
            : navigation.TryGetDescendantsKeysInBin(key, out IEnumerable<Guid> binned) ? binned
            : [];

    // The navigation structure knows no aliases; the entity service reads them without loading content.
    public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys)
        => keys.Chunk(TypesBatch)
            .SelectMany(batch => entities.GetAll(Umbraco.Cms.Core.Models.UmbracoObjectTypes.Document, batch))
            .OfType<Umbraco.Cms.Core.Models.Entities.IContentEntitySlim>()
            .Select(entity => entity.ContentTypeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
