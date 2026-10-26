using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Tags each entry in Umbraco's Delivery API output cache with the keys of what it shows, the same
///     keys its header carries, so a purge evicts exactly the entries it reaches. Umbraco calls it only
///     when output caching is on, once per resolved item, during the request.
/// </summary>
internal sealed class OutputCacheTags(IHttpContextAccessor httpContextAccessor) : IDeliveryApiOutputCacheTagProvider
{
    public IEnumerable<string> GetTags(IPublishedContent content)
    {
        return httpContextAccessor.HttpContext?.Items[CacheKeyResultFilter.OutputCacheTagsItem] as IReadOnlyList<string> ?? [];
    }
}
