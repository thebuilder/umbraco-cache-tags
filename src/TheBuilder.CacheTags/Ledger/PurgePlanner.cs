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
    /// <summary>The most descendants a route change purges one by one before it purges everything instead.</summary>
    public const int MaxDescendants = 500;

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
            if (named.Contains(CacheKeys.All))
            {
                return new PurgePlan([], Everything: true);
            }

            // A named item's type too: a response past MaxItemKeys lists its items' types, not the items,
            // a document's within its site root.
            var planned = new SortedSet<string>(named, StringComparer.Ordinal);
            var documentRoots = new HashSet<Guid>();
            var documents = Ids(named, CacheKeys.ContentPrefix);
            var rooted = new HashSet<Guid>();
            try
            {
                foreach (var (key, alias, root) in descendants.TypesAndRootsOf(documents))
                {
                    planned.Add(CacheKeys.Type(alias));
                    if (root is { } scope)
                    {
                        planned.Add(CacheKeys.Type(alias, scope));
                        documentRoots.Add(scope);
                        rooted.Add(key);
                    }
                }

                planned.UnionWith(descendants.TypesOf(Ids(named, CacheKeys.MediaPrefix), media: true).Select(CacheKeys.Type));
            }
            catch (Exception)
            {
                // Recording must not fail with it: without their types, every response is purged.
                return new PurgePlan([], Everything: true);
            }

            // Only documents belong to a root: anything else named (a custom key, media, a set) may be
            // shown by any site, and so may a document whose root is unknown (in the recycle bin, gone).
            var onlyRootedDocuments = named.All(key => key.StartsWith(CacheKeys.ContentPrefix, StringComparison.Ordinal))
                && documents.All(rooted.Contains);
            return new PurgePlan(planned, Everything: false, onlyRootedDocuments && documentRoots.Count > 0 ? documentRoots : null);
        }

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        if (change.Kind == ChangeKind.TypesChanged)
        {
            // Any response may render an item of the type, so everything. Its listings are named too, so
            // they are purged again once every index has a renamed type (IndexCheck).
            keys.UnionWith(change.TypeAliases.Select(CacheKeys.Type));

            // And within every site root, for listings confined to one root's tree.
            var allRoots = descendants.Roots().ToList();
            keys.UnionWith(change.TypeAliases.SelectMany(alias => allRoots.Select(root => CacheKeys.Type(alias, root))));
            return new PurgePlan(keys, Everything: true);
        }

        var media = change.Kind is ChangeKind.MediaSaved or ChangeKind.MediaMoved or ChangeKind.MediaTrashed or ChangeKind.MediaDeleted;
        var routeDescendants = new HashSet<Guid>();

        // The site roots the documents are under, and were under before a move. A change recorded
        // without them (media, or an older change) may concern any site.
        var roots = new HashSet<Guid>();
        // An item whose roots could not be read may be under any root.
        var rootsKnown = media is false && change.Items.All(item => item.Roots is { Count: > 0 });
        foreach (ChangedItem item in change.Items)
        {
            // A document's type and catch-all keys within each root it is or was under: listings fetched
            // from inside one root carry those, so another root's changes do not reach them.
            IReadOnlyList<Guid> itemRoots = media ? [] : item.Roots ?? [];
            roots.UnionWith(itemRoots);
            if (change.Kind == ChangeKind.Sorted)
            {
                // The items did not change, only their order: in the parent's children, and in every
                // listing ordered by sort order that shows them (a type listing, or any other query).
                keys.Add(CacheKeys.Children(item.ParentKey));
                AddSelectors(keys, item.TypeAlias, ListingSelectors.ContentIndex, itemRoots);
                continue;
            }

            keys.Add(media ? CacheKeys.Media(item.Key) : CacheKeys.Content(item.Key));
            keys.Add(CacheKeys.Children(item.ParentKey));
            if (item.OldParentKey is { } oldParent)
            {
                keys.Add(CacheKeys.Children(oldParent));
            }

            AddSelectors(keys, item.TypeAlias, media ? ListingSelectors.MediaSearch : ListingSelectors.ContentIndex, itemRoots);

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
        // Descendants are under their item's roots, before and after a move.
        keys.UnionWith(routeDescendants.Select(CacheKeys.Content));
        if (routeDescendants.Count > 0)
        {
            try
            {
                foreach (var alias in descendants.TypesOf(routeDescendants, media: false))
                {
                    keys.Add(CacheKeys.Type(alias));
                    keys.UnionWith(roots.Select(root => CacheKeys.Type(alias, root)));
                }
            }
            catch (Exception)
            {
                // Recording must not fail with it: without their types, every listing is purged.
                return new PurgePlan([], Everything: true);
            }
        }

        return new PurgePlan(keys, Everything: false, rootsKnown && roots.Count > 0 ? roots : null);
    }

    // The listing keys an item could join or leave: its type's and the catch-all, globally and within
    // each of its roots.
    private static void AddSelectors(ISet<string> keys, string alias, string index, IReadOnlyList<Guid> roots)
    {
        keys.Add(CacheKeys.Type(alias));
        keys.Add(CacheKeys.Search(index));
        foreach (Guid root in roots)
        {
            keys.Add(CacheKeys.Type(alias, root));
            keys.Add(CacheKeys.Search(index, root));
        }
    }

    // The ids of the named keys with the prefix (content- or media-).
    private static List<Guid> Ids(IEnumerable<string> keys, string prefix)
        => keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(key => Guid.TryParse(key[prefix.Length..], out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
}

/// <summary>A document's descendants, from Umbraco's in-memory navigation structure.</summary>
internal interface IDescendants
{
    IEnumerable<Guid> Of(Guid key);

    /// <summary>The content type aliases of the given documents, or the media type aliases of the given media.</summary>
    IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys, bool media);

    /// <summary>
    ///     Each of the given documents that exists, with its content type alias and the site root it is
    ///     under (none in the recycle bin).
    /// </summary>
    IEnumerable<(Guid Key, string Alias, Guid? Root)> TypesAndRootsOf(IReadOnlyCollection<Guid> keys);

    /// <summary>Every site root: the documents at the top level of the content tree.</summary>
    IEnumerable<Guid> Roots() => [];
}

internal sealed class NavigationDescendants(
    Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService navigation,
    Umbraco.Cms.Core.Services.IEntityService entities,
    SiteRoots siteRoots) : IDescendants
{
    // Below SQL Server's 2100 parameters per query.
    private const int TypesBatch = 1000;

    // A trashed document's descendants moved to the recycle bin with it.
    public IEnumerable<Guid> Of(Guid key)
        => navigation.TryGetDescendantsKeys(key, out IEnumerable<Guid> keys) ? keys
            : navigation.TryGetDescendantsKeysInBin(key, out IEnumerable<Guid> binned) ? binned
            : [];

    // The navigation structure knows no aliases; the entity service reads them without loading content.
    public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys, bool media)
        => keys.Chunk(TypesBatch)
            .SelectMany(batch => entities.GetAll(media ? Umbraco.Cms.Core.Models.UmbracoObjectTypes.Media : Umbraco.Cms.Core.Models.UmbracoObjectTypes.Document, batch))
            .OfType<Umbraco.Cms.Core.Models.Entities.IContentEntitySlim>()
            .Select(entity => entity.ContentTypeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<(Guid Key, string Alias, Guid? Root)> TypesAndRootsOf(IReadOnlyCollection<Guid> keys)
        => keys.Chunk(TypesBatch)
            .SelectMany(batch => entities.GetAll(Umbraco.Cms.Core.Models.UmbracoObjectTypes.Document, batch))
            .OfType<Umbraco.Cms.Core.Models.Entities.IContentEntitySlim>()
            .Select(entity => (entity.Key, entity.ContentTypeAlias, siteRoots.OfPath(entity.Path)))
            .ToList();

    public IEnumerable<Guid> Roots() => navigation.TryGetRootKeys(out IEnumerable<Guid> keys) ? keys : [];
}
