using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
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
///     gives a cacheable response its keys in the response header and queues the record of what it
///     covers. Preview responses get <c>no-store</c> and member responses <c>private</c>, and neither
///     gets a key. Nor does a response other than a 200 (a redirect included), or one whose request
///     headers are too long to record: both get <c>no-store</c>.
/// </summary>
internal sealed class CacheKeyResultFilter(
    IOptionsMonitor<CacheTagsOptions> options,
    IRequestPreviewService preview,
    IRequestCultureService requestCulture,
    IRequestSegmentService requestSegment,
    IRequestStartItemProvider startItem,
    IVariationContextAccessor variationContext,
    ResponseCollector collector,
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
                Tag(context, settings, value => collector.Collect(value, context.HttpContext, endpoint), ResponseIdentity.DeliveryParameters, recordable: true);
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

        ResponseIdentity identity = ResponseIdentity.Create(
            http.Request.Path.Value ?? string.Empty,
            http.Request.Query.SelectMany(pair => pair.Value.Select(v => new KeyValuePair<string, string?>(pair.Key, v))),
            requestCulture.GetRequestedCulture(),
            requestSegment.GetRequestedSegment(),
            startItem.RequestedStartItem(),
            parameters);
        if (identity.Fits is false)
        {
            // Headers too long to record: the response could not be purged, so it must not be cached.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            return;
        }

        var responseKey = identity.ToKey();
        IReadOnlySet<string> keys = collect(value);

        ResponseTags tags = ResponseTags.For(settings.KeyMode, settings.MaxItemKeys, responseKey, keys, collector.TypeKeys, recordable);
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

    /// <summary>
    ///     The status a result answers with, and its body for results that carry one. The response is
    ///     tagged before the result runs, so a result that sets its status only as it runs (a redirect,
    ///     such as the Delivery API's 301 for a moved item, a challenge, a forbid, or one this does not
    ///     know) counts as not a 200: the response still says 200 here.
    /// </summary>
    private static (int Status, object? Value) Outcome(IActionResult result, HttpResponse response) => result switch
    {
        ObjectResult objectResult => (objectResult.StatusCode ?? response.StatusCode, objectResult.Value),
        JsonResult json => (json.StatusCode ?? response.StatusCode, json.Value),
        IStatusCodeActionResult statusCode => (statusCode.StatusCode ?? response.StatusCode, null),
        FileResult or EmptyResult => (response.StatusCode, null),
        _ when Wrapped(result) is { } inner => Outcome(inner, response),
        _ => (0, null),
    };

    /// <summary>An <see cref="IResult" /> (<c>TypedResults</c>, <c>Results&lt;...&gt;</c>), unwrapped down to the one that answers.</summary>
    private static (int Status, object? Value) Outcome(IResult result, HttpResponse response) => result switch
    {
        INestedHttpResult nested => Outcome(nested.Result, response),
        IStatusCodeHttpResult statusCode => (statusCode.StatusCode ?? response.StatusCode, (result as IValueHttpResult)?.Value),
        _ => (0, null),
    };

    // MVC wraps an IResult an action returns in its internal HttpActionResult.
    private static IResult? Wrapped(IActionResult result)
        => result.GetType().GetProperties().FirstOrDefault(property => property.Name == "Result" && typeof(IResult).IsAssignableFrom(property.PropertyType))?.GetValue(result) as IResult;
}
