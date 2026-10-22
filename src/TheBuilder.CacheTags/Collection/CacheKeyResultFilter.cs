using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Runs after every Delivery API action: gives a cacheable response its key in the response
///     header and queues the record of what it covers. Preview responses get <c>no-store</c> and
///     member responses <c>private</c>, and neither gets a key. Nor does a response other than a 200 with a
///     body, or one whose request headers are too long to record: both get <c>no-store</c>.
/// </summary>
internal sealed class CacheKeyResultFilter(
    IOptionsMonitor<CacheTagsOptions> options,
    IRequestPreviewService preview,
    IRequestCultureService requestCulture,
    IRequestSegmentService requestSegment,
    IRequestStartItemProvider startItem,
    IVariationContextAccessor variationContext,
    ResponseCollector collector,
    MappingWriter writer,
    TimeProvider time) : IAsyncResultFilter
{
    /// <summary>Where the response's keys for Umbraco's output cache are kept for the rest of the request.</summary>
    public const string OutputCacheTagsItem = "TheBuilder.CacheTags.OutputCacheTags";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        CacheTagsOptions settings = options.CurrentValue;
        if (settings.Enabled && context.ActionDescriptor is ControllerActionDescriptor action && DeliveryEndpoint.Of(action.ControllerTypeInfo) is { } endpoint)
        {
            Tag(context, endpoint, settings);
        }

        await next();
    }

    private void Tag(ResultExecutingContext context, DeliveryEndpoint endpoint, CacheTagsOptions settings)
    {
        HttpContext http = context.HttpContext;
        if (preview.IsPreview())
        {
            // Drafts must never be cached by the frontend or a CDN.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        if (http.User.Identity?.IsAuthenticated is true || http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // A member's response: the key does not vary by member, so it must not be shared.
            http.Response.Headers[HeaderNames.CacheControl] = "private";
            return;
        }

        if (context.Result is not ObjectResult { Value: { } value } result || (result.StatusCode ?? 200) != 200)
        {
            // A not found or a redirect covers no item, so no purge would reach it: publishing at
            // that path later must not leave it cached.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        ResponseIdentity identity = ResponseIdentity.Create(
            http.Request.Path.Value ?? string.Empty,
            http.Request.Query.SelectMany(pair => pair.Value.Select(v => new KeyValuePair<string, string?>(pair.Key, v))),
            requestCulture.GetRequestedCulture(),
            requestSegment.GetRequestedSegment(),
            startItem.RequestedStartItem());
        if (identity.Fits is false)
        {
            // Headers too long to record: the response could not be purged, so it must not be cached.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        var responseKey = identity.ToKey();
        IReadOnlySet<string> keys = collector.Collect(value, http, endpoint);

        ResponseTags tags = ResponseTags.For(settings.KeyMode, settings.MaxItemKeys, responseKey, keys, collector.TypeKeys);
        http.Response.Headers[settings.HeaderName] = string.Join(", ", tags.Keys.Select(key => CacheKeys.WithPrefix(settings.KeyPrefix, key)));

        // Umbraco's output cache is evicted by the same keys a purge names: the response key, or the
        // keys a response that is not recorded carries itself.
        http.Items[OutputCacheTagsItem] = tags.Recorded ? new[] { responseKey } : tags.Keys;
        if (tags.Recorded is false)
        {
            return;
        }

        // The key hashes the culture as requested; the mapping stores the culture actually served
        // (Accept-Language: da resolves to da-DK, no header to the default), which a culture-limited
        // purge compares against.
        var servedCulture = variationContext.VariationContext?.Culture?.ToLowerInvariant() ?? identity.Culture;
        DateTime now = time.GetUtcNow().UtcDateTime;
        writer.Record(new CachedResponse
        {
            ResponseKey = responseKey,
            Path = identity.Path,
            Query = identity.Query,
            Culture = servedCulture,
            Segment = identity.Segment,
            StartItem = identity.StartItem,
            Keys = keys.Order(StringComparer.Ordinal).ToArray(),
            RenderedUtc = now,
            SeenUtc = now,
        });
    }
}
