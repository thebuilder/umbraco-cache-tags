using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Works out which keys a Delivery API response covers: what its result objects show, what the
///     published values of its resolved items and of the items it expands reference (in the request
///     culture), and, for listings, which selectors a new item would have to match.
///     Reading published source values rather than hooking Umbraco's builders is what keeps this
///     complete on warm requests.
/// </summary>
internal sealed class ResponseCollector(
    ReferenceReader reader,
    IPublishedContentCache contentCache,
    IPublishedMediaCache mediaCache,
    IVariationContextAccessor variationContext,
    IApiContentPathResolver contentPaths,
    IApiMediaQueryService mediaQuery,
    KnownNames names,
    SiteRoots roots)
{
    // Umbraco.Cms.Api.Delivery.Caching.DeliveryApiOutputCacheKeys is internal; these are its values
    // from 17.4.0 on. When they are missing, only the result objects are walked.
    private const string ResolvedContentItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedContentItems";
    private const string ResolvedMediaItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedMediaItems";

    public IReadOnlySet<string> Collect(object? result, HttpContext http, DeliveryEndpoint endpoint)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rendered = new HashSet<Guid>();
        OutputWalker.Collect(result, keys, rendered);
        if (endpoint.ByIds)
        {
            CollectRequestedItems(http.Request.Query["id"], endpoint.Media, keys);
        }

        CollectReferences(http, rendered, keys);
        if (endpoint.Listing)
        {
            keys.UnionWith(ListingSelectors.For(
                http.Request.Query["fetch"].ToString(),
                http.Request.Query["filter"].Where(filter => filter is not null).Select(filter => filter!),
                target => ResolveTarget(target, endpoint.Media),
                alias => names.IsType(alias, endpoint.Media),
                endpoint.Media,
                RootOf));
        }

        return keys;
    }

    /// <summary>
    ///     Items asked for by id (<c>items?id=…</c>): a later publish of one the response could not show
    ///     yet must reach it, so it covers every id asked for.
    /// </summary>
    /// <param name="ids">The <c>id</c> parameters.</param>
    /// <param name="media">Whether the ids are media.</param>
    /// <param name="keys">The keys collected from what the response shows, added to.</param>
    public static void CollectRequestedItems(IEnumerable<string?> ids, bool media, ISet<string> keys)
    {
        foreach (var value in ids)
        {
            if (Guid.TryParse(value, out Guid id))
            {
                keys.Add(media ? CacheKeys.Media(id) : CacheKeys.Content(id));
            }
        }
    }

    private void CollectReferences(HttpContext http, IReadOnlySet<Guid> rendered, HashSet<string> keys)
    {
        IPublishedContent[] resolved =
        [
            .. http.Items[ResolvedContentItemsKey] as IPublishedContent[] ?? [],
            .. http.Items[ResolvedMediaItemsKey] as IPublishedContent[] ?? [],
        ];
        if (resolved.Length == 0)
        {
            return;
        }

        var culture = variationContext.VariationContext?.Culture;
        foreach (IPublishedContent item in resolved)
        {
            keys.Add(item.ItemType == PublishedItemType.Media ? CacheKeys.Media(item.Key) : CacheKeys.Content(item.Key));
            keys.UnionWith(reader.Read(item, culture));
        }

        var requested = resolved.Select(item => item.Key).ToHashSet();
        ReadRendered(rendered.Where(id => requested.Contains(id) is false), culture, keys);
    }

    /// <summary>
    ///     A site's own action (<see cref="CacheTaggedAttribute" />): the Delivery API items its result
    ///     shows, and what the documents it renders with their properties reference, as for a Delivery
    ///     API response. Nothing is resolved for it, so its result is all there is to go by.
    /// </summary>
    public void CollectShown(object? result, ISet<string> keys)
    {
        var rendered = new HashSet<Guid>();
        OutputWalker.Collect(result, keys, rendered);
        if (rendered.Count > 0)
        {
            ReadRendered(rendered, variationContext.VariationContext?.Culture, keys);
        }
    }

    // An expanded item renders its properties, and so what they reference, at any depth of
    // expansion. Only the items the result shows with their properties count: a link, or a picker
    // the request does not expand, shows the item alone, however much the item itself references.
    private void ReadRendered(IEnumerable<Guid> rendered, string? culture, ISet<string> keys)
    {
        foreach (Guid id in rendered)
        {
            if (contentCache.GetById(id) is { } item)
            {
                keys.UnionWith(reader.Read(item, culture));
            }
        }
    }

    private Guid? ResolveTarget(string target, bool media)
    {
        // An id that names nothing is like a path that does: nothing selects the listing yet, so it is
        // a catch-all, which every publish purges, the one that creates the item included.
        if (Guid.TryParse(target, out Guid key))
        {
            IPublishedContent? byKey = media ? mediaCache.GetById(key) : contentCache.GetById(key);
            return byKey?.Key;
        }

        // "children:/" lists the children of the item routed at "/" (the site's root page). Only when
        // nothing is routed there does it mean the root level itself.
        IPublishedContent? item = media ? mediaQuery.GetByPath(target) : contentPaths.ResolveContentPath(target);
        return item?.Key ?? (target is "/" or "" ? Guid.Empty : null);
    }

    /// <summary>
    ///     The types of the given items, which a change to any of them purges: a document's within its
    ///     site root, so another root's changes do not reach it. An item that is not in the published
    ///     cache (not published yet, or gone) keeps its own key: its publish plans that key, and its type
    ///     is unknown here.
    /// </summary>
    public SortedSet<string> TypeKeys(IEnumerable<string> itemKeys)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in itemKeys)
        {
            IPublishedContent? item = key.StartsWith(CacheKeys.MediaPrefix, StringComparison.Ordinal)
                ? mediaCache.GetById(Guid.Parse(key[CacheKeys.MediaPrefix.Length..]))
                : contentCache.GetById(Guid.Parse(key[CacheKeys.ContentPrefix.Length..]));
            if (item is null)
            {
                types.Add(key);
            }
            else if (item.ItemType == PublishedItemType.Content && roots.OfPath(item.Path) is { } root)
            {
                // Its root's type key, which only a change under that root plans.
                types.Add(CacheKeys.Type(item.ContentType.Alias, root));
            }
            else
            {
                types.Add(CacheKeys.Type(item.ContentType.Alias));
            }
        }

        return types;
    }

    // The site root a published document is under, for listings fetched from it.
    private Guid? RootOf(Guid key) => contentCache.GetById(key) is { } item ? roots.OfPath(item.Path) : null;
}

/// <summary>
///     Which Delivery API endpoint served a request: content or media, and a listing (<c>Query…</c>,
///     reads <c>fetch</c> and <c>filter</c>), several items by id (<c>ByIds…</c>, reads <c>id</c>) or
///     one item by route or id (reads neither).
/// </summary>
internal sealed record DeliveryEndpoint(bool Media, bool Listing, bool ByIds = false)
{
    /// <summary>The Delivery API's own controllers, recognised by namespace and name; anything else is not one.</summary>
    public static DeliveryEndpoint? Of(Type controller)
        => controller.Namespace?.StartsWith("Umbraco.Cms.Api.Delivery.Controllers", StringComparison.Ordinal) is true
            ? new DeliveryEndpoint(
                controller.Name.Contains("Media", StringComparison.Ordinal),
                controller.Name.StartsWith("Query", StringComparison.Ordinal),
                controller.Name.StartsWith("ByIds", StringComparison.Ordinal))
            : null;
}
