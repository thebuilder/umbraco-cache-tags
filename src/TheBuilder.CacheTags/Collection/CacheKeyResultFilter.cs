using System.Reflection;
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
///     Runs after every Delivery API action, and every action with <see cref="CacheTaggedAttribute" />:
///     queues the record of what a cacheable response covers, then gives the response its keys in the
///     header (the keys themselves rather than its response key when the record could not be
///     queued). Preview responses get <c>no-store</c> and member responses <c>private</c>, and neither
///     gets a key. Nor does a response other than a 200, or one whose request headers are too long to
///     record: both get <c>no-store</c>.
/// </summary>
internal sealed class CacheKeyResultFilter(
    IOptionsMonitor<CacheTagsOptions> options,
    IRequestPreviewService preview,
    IRequestCultureService requestCulture,
    IRequestStartItemProvider startItem,
    IVariationContextAccessor variationContext,
    ResponseCollector collector,
    KnownNames names,
    CacheTagsResponse named,
    MappingWriter writer,
    TimeProvider time) : IAsyncResultFilter
{
    /// <summary>Where the response's keys for Umbraco's output cache are kept for the rest of the request.</summary>
    public const string OutputCacheTagsItem = "TheBuilder.CacheTags.OutputCacheTags";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        CacheTagsOptions settings = options.CurrentValue;
        if (settings.Enabled && context.ActionDescriptor is ControllerActionDescriptor action)
        {
            if (DeliveryEndpoint.Of(action.ControllerTypeInfo) is { } endpoint)
            {
                Tag(context, settings, value => collector.Collect(value, context.HttpContext, endpoint), endpoint.Listing ? ResponseIdentity.ListingParameters : ResponseIdentity.ItemParameters, recordable: true);
            }
            else if ((action.MethodInfo.GetCustomAttribute<CacheTaggedAttribute>() ?? action.ControllerTypeInfo.GetCustomAttribute<CacheTaggedAttribute>()) is { } tagged)
            {
                Tag(context, settings, Custom, new HashSet<string>(tagged.QueryParameters, StringComparer.OrdinalIgnoreCase), tagged.Recorded);
            }
        }

        await next();
    }

    // A site's own action: the keys it named, and the Delivery API items in its result.
    private IReadOnlySet<string> Custom(object? value)
    {
        var keys = new HashSet<string>(named.Keys, StringComparer.Ordinal);
        OutputWalker.Collect(value, keys);
        return keys;
    }

    private void Tag(ResultExecutingContext context, CacheTagsOptions settings, Func<object?, IReadOnlySet<string>> collect, IReadOnlySet<string> parameters, bool recordable)
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

        (var status, var value) = Outcome(context.Result, http.Response);
        if (status != 200)
        {
            // A not found or a redirect covers no item, so no purge would reach it: publishing at
            // that path later must not leave it cached.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        // Umbraco takes the culture and segment headers as sent, so they are input that can vary
        // without end. The segment is left out of the key: segment variants share it, which is safe
        // because no purge is limited by segment. The start item is keyed as the item it resolves to.
        var culture = variationContext.VariationContext?.Culture ?? requestCulture.GetRequestedCulture();
        ResponseIdentity identity = ResponseIdentity.Create(
            http.Request.Path.Value ?? string.Empty,
            http.Request.Query.SelectMany(pair => pair.Value.Select(v => new KeyValuePair<string, string?>(pair.Key, v))),
            culture,
            segment: null,
            startItem.GetStartItem()?.Key.ToString("D"),
            parameters);
        if (identity.Fits is false)
        {
            // Headers too long to record: the response could not be purged, so it must not be cached.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        var responseKey = identity.ToKey();
        IReadOnlySet<string> keys = collect(value);

        // A culture that is no language is not recorded: a purge limited to a language would miss it,
        // and its values would make a mapping row each. It carries its keys itself.
        recordable = recordable && (string.IsNullOrEmpty(culture) || names.IsCulture(culture));
        ResponseTags tags = ResponseTags.For(settings.KeyMode, settings.MaxItemKeys, responseKey, keys, collector.TypeKeys, recordable);
        if (tags.Recorded && Record(identity, responseKey, keys) is false)
        {
            // The mapping write could not be queued, so no purge would find the response by its key:
            // it carries its keys itself instead, like a response that is never recorded.
            tags = ResponseTags.For(settings.KeyMode, settings.MaxItemKeys, responseKey, keys, collector.TypeKeys, recordable: false);
        }

        http.Response.Headers[settings.HeaderName] = string.Join(", ", tags.Keys.Select(key => CacheKeys.WithPrefix(settings.KeyPrefix, key)));

        // Umbraco's output cache is evicted by the same keys a purge names: the response key, or the
        // keys a response that is not recorded carries itself.
        http.Items[OutputCacheTagsItem] = tags.Recorded ? new[] { responseKey } : tags.Keys;
    }

    /// <summary>Queues the record of what the response covers, before its key goes out. False when it could not be queued.</summary>
    private bool Record(ResponseIdentity identity, string responseKey, IReadOnlySet<string> keys)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        return writer.Record(new CachedResponse
        {
            ResponseKey = responseKey,
            Path = identity.Path,
            Query = identity.Query,
            Culture = identity.Culture,
            Segment = identity.Segment,
            StartItem = identity.StartItem,
            Keys = keys.Order(StringComparer.Ordinal).ToArray(),
            RenderedUtc = now,
            SeenUtc = now,
        });
    }

    /// <summary>The status a result answers with, and its body for results that carry one.</summary>
    private static (int Status, object? Value) Outcome(IActionResult result, HttpResponse response) => result switch
    {
        ObjectResult objectResult => (objectResult.StatusCode ?? 200, objectResult.Value),
        JsonResult json => (json.StatusCode ?? 200, json.Value),
        ContentResult content => (content.StatusCode ?? 200, null),
        StatusCodeResult statusCode => (statusCode.StatusCode, null),
        _ => (response.StatusCode, null),
    };
}
