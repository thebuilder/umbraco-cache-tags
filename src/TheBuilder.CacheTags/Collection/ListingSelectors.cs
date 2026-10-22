using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     The selector keys of a Delivery API query (listing): what a newly published item would have to
///     match to appear in it. A publish plans the same selectors, so it reaches every listing the item
///     could join, not only the ones it is already in.
/// </summary>
internal static class ListingSelectors
{
    /// <summary>The Delivery API content index (<c>Constants.UmbracoIndexes.DeliveryApiContentIndexName</c>).</summary>
    public const string ContentIndex = "DeliveryApiContentIndex";

    /// <summary>The key media listings share; media queries walk the media cache rather than an index.</summary>
    public const string MediaSearch = "media";

    /// <summary>The most type aliases a listing is selected by; one filtered by more is a catch-all.</summary>
    public const int MaxTypes = 10;

    /// <param name="fetch">The <c>fetch</c> parameter, such as <c>children:/articles/</c> or <c>descendants:&lt;id&gt;</c>.</param>
    /// <param name="filters">The <c>filter</c> parameters, such as <c>contentType:article</c>.</param>
    /// <param name="resolve">
    ///     Resolves the id or path after <c>children:</c> to the item's key, <see cref="Guid.Empty" /> for
    ///     the root, or <c>null</c> when no such item exists.
    /// </param>
    /// <param name="isType">Whether a content or media type with the given alias exists.</param>
    /// <param name="media">Whether this is the media query endpoint.</param>
    public static IReadOnlyCollection<string> For(string? fetch, IEnumerable<string> filters, Func<string, Guid?> resolve, Func<string, bool> isType, bool media)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var catchAll = CacheKeys.Search(media ? MediaSearch : ContentIndex);
        var typeFilter = media ? "mediaType:" : "contentType:";
        var filterList = filters.ToList();
        var (axis, target) = Split(fetch);
        Guid? targetKey = target is null ? null : resolve(target);

        // A fetch of an item that does not exist matches nothing until something is published there.
        if (target is not null && targetKey is null)
        {
            keys.Add(catchAll);
            return keys;
        }

        // A filter on anything but the type (a name, a search, a custom filter) narrows the listing by
        // input that can vary without end. The listing is then a catch-all as well as whatever else
        // selects it, so it is not recorded (see ResponseTags) and every publish purges it.
        if (filterList.Any(filter => filter.StartsWith(typeFilter, StringComparison.OrdinalIgnoreCase) is false))
        {
            keys.Add(catchAll);
        }

        // The fetched item itself: an ancestors listing changes when its own route does.
        if (targetKey is { } fetched && fetched != Guid.Empty && axis is "children" or "descendants" or "ancestors")
        {
            keys.Add(media ? CacheKeys.Media(fetched) : CacheKeys.Content(fetched));
        }

        if (axis == "children" && targetKey is { } parent)
        {
            keys.Add(CacheKeys.Children(parent));
            return keys;
        }

        if (axis == "ancestors")
        {
            // The ancestors are in the result and so already covered as items.
            return keys;
        }

        var types = filterList
            .Where(f => f.StartsWith(typeFilter, StringComparison.OrdinalIgnoreCase))
            .Select(f => f[typeFilter.Length..])
            .SelectMany(v => v.Split(','))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only aliases that name a type select a listing, and only a few of them: any other alias, or
        // more of them, is input that can vary without end, and would make a mapping row each.
        if (types.Count is > 0 and <= MaxTypes && types.All(t => t.Length > 0 && t.StartsWith('!') is false && isType(t)))
        {
            foreach (var type in types)
            {
                keys.Add(CacheKeys.Type(type));
            }

            return keys;
        }

        keys.Add(catchAll);
        return keys;
    }

    private static (string? Axis, string? Target) Split(string? fetch)
    {
        if (string.IsNullOrWhiteSpace(fetch))
        {
            return (null, null);
        }

        var colon = fetch.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (fetch.Trim().ToLowerInvariant(), null)
            : (fetch[..colon].Trim().ToLowerInvariant(), fetch[(colon + 1)..].Trim());
    }
}
