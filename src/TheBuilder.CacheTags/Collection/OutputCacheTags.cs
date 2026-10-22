using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Tags each entry in Umbraco's Delivery API output cache with its <c>response-</c> key (or, for a
///     response that is not recorded, the keys it carries), so the package can evict exactly that entry. Umbraco calls it only when output caching is on, once per
///     resolved item, during the request.
/// </summary>
internal sealed class OutputCacheTags(IHttpContextAccessor httpContextAccessor) : IDeliveryApiOutputCacheTagProvider
{
    public IEnumerable<string> GetTags(IPublishedContent content)
    {
        return httpContextAccessor.HttpContext?.Items[CacheKeyResultFilter.OutputCacheTagsItem] as IReadOnlyList<string> ?? [];
    }
}
