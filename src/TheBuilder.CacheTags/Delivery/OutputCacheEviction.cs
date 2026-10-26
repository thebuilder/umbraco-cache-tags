using TheBuilder.CacheTags.Ledger;
using IDeliveryApiOutputCacheManager = Umbraco.Cms.Core.Cache.IDeliveryApiOutputCacheManager;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Evicts responses from this server's Delivery API output cache, when output caching is on. The
///     package tags each cached entry with the keys of what it shows (see <c>OutputCacheTags</c>).
/// </summary>
internal sealed class OutputCacheEviction(IServiceProvider services)
{
    // Umbraco registers the manager only when output caching is enabled.
    private readonly IDeliveryApiOutputCacheManager? _outputCache = services.GetService<IDeliveryApiOutputCacheManager>();

    public bool Enabled => _outputCache is not null;

    public async Task EvictAsync(PurgePlan plan)
    {
        if (_outputCache is null)
        {
            return;
        }

        if (plan.Everything)
        {
            await _outputCache.EvictAllAsync();
            return;
        }

        await EvictAsync(plan.Keys);
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
