using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Works out which keys a Delivery API response covers: what its result objects show, what its
///     resolved items' published values reference (in the request culture, one level further for
///     expanded properties), and, for listings, which selectors a new item would have to match.
///     Reading published source values rather than hooking Umbraco's builders is what keeps this
///     complete on warm requests (spike/FINDINGS.md, section 1).
/// </summary>
internal sealed class ResponseCollector(
    ReferenceReader reader,
    IPublishedContentCache contentCache,
    IPublishedMediaCache mediaCache,
    IVariationContextAccessor variationContext,
    IApiContentPathResolver contentPaths,
    IApiMediaQueryService mediaQuery)
{
    // Umbraco.Cms.Api.Delivery.Caching.DeliveryApiOutputCacheKeys is internal; these are its values
    // from 17.4.0 on. When they are missing, only the result objects are walked.
    private const string ResolvedContentItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedContentItems";
    private const string ResolvedMediaItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedMediaItems";

    /// <summary>The most keys one response is recorded with.</summary>
    public const int MaxKeys = 1000;

    public IReadOnlySet<string> Collect(object result, HttpContext http, DeliveryEndpoint endpoint)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        OutputWalker.Collect(result, keys);
        CollectReferences(http, keys);
        if (endpoint.Listing)
        {
            keys.UnionWith(ListingSelectors.For(
                http.Request.Query["fetch"].ToString(),
                http.Request.Query["filter"].Where(filter => filter is not null).Select(filter => filter!),
                target => ResolveTarget(target, endpoint.Media),
                endpoint.Media));
        }

        if (keys.Count > MaxKeys)
        {
            // A request can ask for many items (take, expand); past this, the items are recorded as
            // their types, which a change to any of them purges, to bound the mapping per response.
            var items = keys.Where(CacheKeys.IsItem).ToList();
            keys.ExceptWith(items);
            keys.UnionWith(TypeKeys(items));
        }

        return keys;
    }

    /// <summary>The header value: the response key, or in direct mode the item keys (or their types, past the budget) and selectors.</summary>
    public string Header(CacheTagsOptions options, string responseKey, IReadOnlySet<string> keys)
    {
        if (options.KeyMode == CacheKeyMode.ResponseKey)
        {
            return CacheKeys.WithPrefix(options.KeyPrefix, responseKey);
        }

        var items = keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList();
        IEnumerable<string> shown = items.Count <= options.MaxItemKeys ? items : TypeKeys(items);
        return string.Join(", ", new[] { responseKey }
            .Concat(shown)
            .Concat(keys.Where(key => CacheKeys.IsSelector(key) || CacheKeys.IsSchema(key)).Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(key => CacheKeys.WithPrefix(options.KeyPrefix, key)));
    }

    private void CollectReferences(HttpContext http, HashSet<string> keys)
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
        Expansion expansion = Expansion.Parse(http.Request.Query["expand"].ToString());
        foreach (IPublishedContent item in resolved)
        {
            keys.Add(item.ItemType == PublishedItemType.Media ? CacheKeys.Media(item.Key) : CacheKeys.Content(item.Key));
            foreach (ReferencedKey reference in reader.Read(item, culture))
            {
                keys.Add(reference.Key);

                // An expanded picker renders the picked item's properties, and so what they reference.
                if (reference.IsMedia is false && expansion.Expands(reference.PropertyAlias) && contentCache.GetById(reference.Id) is { } expanded)
                {
                    keys.UnionWith(reader.Read(expanded, culture).Select(r => r.Key));
                }
            }
        }
    }

    private Guid? ResolveTarget(string target, bool media)
    {
        if (Guid.TryParse(target, out Guid key))
        {
            return key;
        }

        // "children:/" lists the children of the item routed at "/" (the site's root page). Only when
        // nothing is routed there does it mean the root level itself.
        IPublishedContent? item = media ? mediaQuery.GetByPath(target) : contentPaths.ResolveContentPath(target);
        return item?.Key ?? (target is "/" or "" ? Guid.Empty : null);
    }

    // Past the direct-mode budget: the types of every item, which a publish of any of them purges.
    private SortedSet<string> TypeKeys(IEnumerable<string> itemKeys)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in itemKeys)
        {
            IPublishedContent? item = key.StartsWith(CacheKeys.MediaPrefix, StringComparison.Ordinal)
                ? mediaCache.GetById(Guid.Parse(key[CacheKeys.MediaPrefix.Length..]))
                : contentCache.GetById(Guid.Parse(key[CacheKeys.ContentPrefix.Length..]));
            types.Add(item is null ? CacheKeys.All : CacheKeys.Type(item.ContentType.Alias));
        }

        return types;
    }
}

/// <summary>Which Delivery API endpoint served a request: content or media, item or listing.</summary>
internal sealed record DeliveryEndpoint(bool Media, bool Listing)
{
    /// <summary>The Delivery API's own controllers, recognised by namespace and name; anything else is not one.</summary>
    public static DeliveryEndpoint? Of(Type controller)
        => controller.Namespace?.StartsWith("Umbraco.Cms.Api.Delivery.Controllers", StringComparison.Ordinal) is true
            ? new DeliveryEndpoint(controller.Name.Contains("Media", StringComparison.Ordinal), controller.Name.StartsWith("Query", StringComparison.Ordinal))
            : null;
}
