using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Collection;

/// <summary>How a response is tagged: the keys in its header, and whether it is recorded in the mapping.</summary>
/// <param name="Keys">The header's keys, without the configured prefix.</param>
/// <param name="Recorded">Whether the response is recorded, so a purge reaches it through its <c>response-</c> key.</param>
internal sealed record ResponseTags(IReadOnlyList<string> Keys, bool Recorded)
{
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
            return new ResponseTags([responseKey, CacheKeys.All], Recorded: true);
        }

        var items = keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList();
        IEnumerable<string> own = catchAll ? [] : [responseKey];
        return new ResponseTags(
            own
                .Concat(items.Count <= maxItemKeys ? items : typesOf(items))
                .Concat(keys.Where(key => CacheKeys.IsSelector(key) || CacheKeys.IsSchema(key) || CacheKeys.IsCustom(key)).Order(StringComparer.Ordinal))
                .Append(CacheKeys.All)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Recorded: catchAll is false);
    }
}
