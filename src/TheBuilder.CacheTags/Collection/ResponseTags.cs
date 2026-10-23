using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Collection;

/// <summary>How a response is tagged: the keys in its header, and whether it is recorded in the mapping.</summary>
/// <param name="Keys">The header's keys, without the configured prefix, at most <see cref="MaxHeaderKeys" />.</param>
/// <param name="Recorded">Whether the response is recorded, so a purge reaches it through its <c>response-</c> key.</param>
/// <param name="AllKeys">Every key the response carries, for Umbraco's output cache, which keeps any number of tags.</param>
internal sealed record ResponseTags(IReadOnlyList<string> Keys, bool Recorded, IReadOnlyList<string> AllKeys)
{
    /// <summary>
    ///     The most keys a header carries. Next.js keeps 128 tags per cache entry and silently drops the
    ///     rest, so a longer header would lose whichever keys came last.
    /// </summary>
    public const int MaxHeaderKeys = 128;

    /// <summary>
    ///     A response normally carries its <c>response-</c> key, recorded with what it covers. A listing
    ///     only the catch-all <c>search-</c> key selects (a search, a name filter, any other query) is
    ///     different: it comes in endless variations, each of which would be one more mapping row, and
    ///     every publish purges all of them. It carries its keys itself instead (its items, or their
    ///     types past <paramref name="maxItemKeys" />, and its selectors), so one <c>search-</c> key
    ///     purges every such response, however many are cached. Every response also carries
    ///     <c>umbraco</c>, which a change to everything purges.
    /// </summary>
    /// <param name="mode">Which keys the header carries for a recorded response.</param>
    /// <param name="maxItemKeys">The most item keys a header carries before it carries their types.</param>
    /// <param name="responseKey">The response's <c>response-</c> key.</param>
    /// <param name="keys">Everything the response covers.</param>
    /// <param name="typesOf">The <c>type-</c> keys of the given items.</param>
    /// <param name="recordable">Whether the endpoint allows recording at all (<see cref="CacheTaggedAttribute.Recorded" />).</param>
    public static ResponseTags For(CacheKeyMode mode, int maxItemKeys, string responseKey, IReadOnlySet<string> keys, Func<IReadOnlyList<string>, IEnumerable<string>> typesOf, bool recordable = true)
    {
        var catchAll = recordable is false || keys.Any(CacheKeys.IsSearch);
        if (mode == CacheKeyMode.ResponseKey && catchAll is false)
        {
            string[] recorded = [responseKey, CacheKeys.All];
            return new ResponseTags(recorded, Recorded: true, recorded);
        }

        // The keys purges need most come first, so a frontend that drops tags past its limit drops the
        // least needed: umbraco (a change to everything), the selectors a publish reaches listings by,
        // the site's own x- keys, then the items. Schema keys come last, because only a type or data
        // type change purges them.
        var items = keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList();
        IEnumerable<string> own = catchAll ? [] : [responseKey];
        var all = own
            .Append(CacheKeys.All)
            .Concat(keys.Where(CacheKeys.IsSelector).Order(StringComparer.Ordinal))
            .Concat(keys.Where(CacheKeys.IsCustom).Order(StringComparer.Ordinal))
            .Concat(items.Count <= maxItemKeys ? items : typesOf(items))
            .Concat(keys.Where(CacheKeys.IsSchema).Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ResponseTags([.. all.Take(MaxHeaderKeys)], Recorded: catchAll is false, all);
    }
}
