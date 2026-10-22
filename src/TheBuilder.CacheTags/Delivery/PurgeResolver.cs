using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using IDeliveryApiOutputCacheManager = Umbraco.Cms.Core.Cache.IDeliveryApiOutputCacheManager;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>A plan resolved to the response keys it reaches now.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Responses">Every response the plan reaches.</param>
/// <param name="Listings">The responses only its listing selectors reach, which are purged again as the index catches up.</param>
internal sealed record ResolvedPurge(PurgePlan Plan, IReadOnlyList<string> Responses, IReadOnlyList<string> Listings)
{
    /// <summary>The keys sent to a target: the responses, then the planned keys (which direct mode uses).</summary>
    public IReadOnlyList<string> Keys(string prefix)
        => Plan.Everything ? [CacheKeys.WithPrefix(prefix, CacheKeys.All)] : Prefixed(Responses.Concat(Plan.Keys), prefix);

    /// <summary>
    ///     The keys of the listing purge, once every server's index has the change: the listing
    ///     responses and the selectors, which reach the catch-all listings that carry their keys
    ///     themselves (see <c>ResponseTags</c>) even when no listing response is recorded.
    /// </summary>
    public IReadOnlyList<string> ListingKeys(string prefix)
        => Prefixed(Listings.Concat(Plan.Keys.Where(CacheKeys.IsSelector)), prefix);

    /// <summary>What to evict from Umbraco's output cache now: the responses, and the planned keys unrecorded responses carry.</summary>
    public IEnumerable<string> OutputCacheTags => Responses.Concat(Plan.Keys);

    /// <summary>What to evict from Umbraco's output cache once the index has the change: the listings, and the selectors.</summary>
    public IEnumerable<string> ListingOutputCacheTags => Listings.Concat(Plan.Keys.Where(CacheKeys.IsSelector));

    private static List<string> Prefixed(IEnumerable<string> keys, string prefix)
        => keys.Distinct(StringComparer.Ordinal).Select(key => CacheKeys.WithPrefix(prefix, key)).ToList();
}

/// <summary>Resolves plans against the mapping store.</summary>
internal sealed class PurgeResolver(ICacheTagStore store)
{
    public async Task<ResolvedPurge> ResolveAsync(PurgePlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Everything)
        {
            return new ResolvedPurge(plan, [], []);
        }

        IReadOnlyCollection<string> responses = await store.FindResponseKeysAsync(plan.Keys, plan.Cultures, cancellationToken);
        var selectors = plan.Keys.Where(CacheKeys.IsSelector).ToList();
        IReadOnlyCollection<string> listings = selectors.Count == 0 ? [] : await store.FindResponseKeysAsync(selectors, plan.Cultures, cancellationToken);
        return new ResolvedPurge(plan, responses.Order(StringComparer.Ordinal).ToList(), listings.Order(StringComparer.Ordinal).ToList());
    }
}

/// <summary>
///     Evicts responses from this server's Delivery API output cache, when output caching is on. The
///     package tags each cached entry with its response key (see <c>ResponseKeyOutputCacheTags</c>).
/// </summary>
internal sealed class OutputCacheEviction(IServiceProvider services)
{
    // Umbraco registers the manager only when output caching is enabled.
    private readonly IDeliveryApiOutputCacheManager? _outputCache = services.GetService<IDeliveryApiOutputCacheManager>();

    public bool Enabled => _outputCache is not null;

    public async Task EvictAsync(ResolvedPurge purge)
    {
        if (_outputCache is null)
        {
            return;
        }

        if (purge.Plan.Everything)
        {
            await _outputCache.EvictAllAsync();
            return;
        }

        await EvictAsync(purge.OutputCacheTags);
    }

    public async Task EvictAsync(IEnumerable<string> tags)
    {
        if (_outputCache is null)
        {
            return;
        }

        foreach (var tag in tags.Distinct(StringComparer.Ordinal))
        {
            await _outputCache.EvictByTagAsync(tag);
        }
    }
}
