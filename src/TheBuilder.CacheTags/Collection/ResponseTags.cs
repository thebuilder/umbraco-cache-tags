using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     The keys a response carries, so a purge of any of them reaches it. Every response carries
///     <c>umbraco</c>, which a change to everything purges, then the keys purges need most, so a header
///     cut at its limit loses the least needed: the selectors a publish reaches listings by, the site's
///     own <c>x-</c> keys, then the items.
/// </summary>
/// <param name="First">
///     <c>umbraco</c>, the selectors and the custom keys, in that order.
/// </param>
/// <param name="Items">The <c>content-</c> and <c>media-</c> keys of what the response shows, in order.</param>
internal sealed record ResponseTags(IReadOnlyList<string> First, IReadOnlyList<string> Items)
{
    /// <summary>The most item keys a response lists before it lists one set (or their types) instead.</summary>
    public const int MaxItemKeys = 64;

    public static ResponseTags Of(IReadOnlySet<string> keys) => new(
        new[] { CacheKeys.All }
            .Concat(keys.Where(CacheKeys.IsSelector).Order(StringComparer.Ordinal))
            .Concat(keys.Where(CacheKeys.IsCustom).Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList(),
        keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList());

    /// <summary>Every key, items included: what Umbraco's output cache, which has no limit, tags the response with.</summary>
    public IReadOnlyList<string> All => With(Items);

    /// <summary>
    ///     Whether the items are listed one by one: at most <paramref name="maxItemKeys" /> of them, and
    ///     with the keys before them within <paramref name="maxHeaderKeys" />.
    /// </summary>
    public bool Fits(int maxItemKeys, int maxHeaderKeys) => Items.Count <= maxItemKeys && First.Count + Items.Count <= maxHeaderKeys;

    /// <summary>
    ///     The header's keys: the items when they fit, otherwise what stands for them, either their
    ///     stored set's <c>set-</c> key or their <c>type-</c> keys. Every change to an item purges its
    ///     sets and plans its type, so the response is still reached.
    /// </summary>
    /// <param name="maxItemKeys">The most item keys a response lists.</param>
    /// <param name="maxHeaderKeys">The most keys a header carries.</param>
    /// <param name="overflow">What the items are listed as when they do not fit.</param>
    public async Task<IReadOnlyList<string>> HeaderAsync(int maxItemKeys, int maxHeaderKeys, Func<IReadOnlyList<string>, Task<IEnumerable<string>>> overflow)
        => Items.Count == 0 || Fits(maxItemKeys, maxHeaderKeys) ? All : With(await overflow(Items));

    private List<string> With(IEnumerable<string> items) => First.Concat(items).Distinct(StringComparer.Ordinal).ToList();
}
