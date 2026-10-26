using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Collection;

/// <summary>The keys a response carries, so a purge of any of them reaches it.</summary>
internal static class ResponseTags
{
    /// <summary>The most item keys a response lists before it lists their types instead.</summary>
    public const int MaxItemKeys = 64;

    /// <summary>
    ///     Every response carries <c>umbraco</c>, which a change to everything purges, then the keys
    ///     purges need most, so a header cut at its limit loses the least needed: the selectors a
    ///     publish reaches listings by, the site's own <c>x-</c> keys, then the items. Items that would
    ///     not fit are listed as their types: past <paramref name="maxItemKeys" />, or past
    ///     <paramref name="maxHeaderKeys" /> with the keys before them. Every change to an item plans
    ///     its type, so a response that lists types is still reached.
    /// </summary>
    /// <param name="maxItemKeys">The most item keys a response lists before it lists their types.</param>
    /// <param name="maxHeaderKeys">The most keys a header carries.</param>
    /// <param name="keys">Everything the response covers.</param>
    /// <param name="typesOf">The <c>type-</c> keys of the given items.</param>
    public static IReadOnlyList<string> For(int maxItemKeys, int maxHeaderKeys, IReadOnlySet<string> keys, Func<IReadOnlyList<string>, IEnumerable<string>> typesOf)
    {
        var items = keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList();
        var first = new[] { CacheKeys.All }
            .Concat(keys.Where(CacheKeys.IsSelector).Order(StringComparer.Ordinal))
            .Concat(keys.Where(CacheKeys.IsCustom).Order(StringComparer.Ordinal))
            .ToList();
        var fits = items.Count <= maxItemKeys && first.Count + items.Count <= maxHeaderKeys;
        return first
            .Concat(fits ? items : typesOf(items))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
