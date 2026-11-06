using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Keys;
using Umbraco.Cms.Core.DeliveryApi;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Runs after every Delivery API action, and every action with <see cref="CacheTaggedAttribute" />:
///     gives a cacheable response the keys of what it shows, in its header and on its entry in
///     Umbraco's output cache. Preview responses get <c>no-store</c> and member responses
///     <c>private</c>, and neither gets a key. Nor does a response other than a 200 (a redirect
///     included), which gets <c>no-store</c>.
/// </summary>
internal sealed class CacheKeyResultFilter(
    IOptionsMonitor<CacheTagsOptions> options,
    IRequestPreviewService preview,
    ResponseCollector collector,
    CacheTagsResponse named,
    KeySetWriter keySets) : IAsyncResultFilter
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
                await TagAsync(context, settings, value => collector.Collect(value, context.HttpContext, endpoint), sets: endpoint.Listing is false);
            }
            else if ((action.MethodInfo.GetCustomAttribute<CacheTaggedAttribute>() ?? action.ControllerTypeInfo.GetCustomAttribute<CacheTaggedAttribute>()) is not null)
            {
                await TagAsync(context, settings, Custom, sets: true);
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

    // sets: whether items too many to list may be one stored set. A listing's own selector key already
    // reaches it when an item joins or leaves, and every page and filter of a listing would store a
    // set of its own, so listings list their items' types.
    private async Task TagAsync(ResultExecutingContext context, CacheTagsOptions settings, Func<object?, IReadOnlySet<string>> collect, bool sets)
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
            // A member's response: its keys do not vary by member, so it must not be shared.
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

        var tags = ResponseTags.Of(collect(value));
        IReadOnlyList<string> header = await tags.HeaderAsync(ResponseTags.MaxItemKeys, settings.MaxHeaderKeys, items => OverflowAsync(items, settings.KeySets && sets));

        // A frontend keeps only so many tags per entry and drops the rest, so the header stops at the
        // limit. Umbraco's output cache has no limit, so its entry is tagged with every key, each item
        // included, and a purge of an item evicts it without the item's sets.
        http.Response.Headers[settings.HeaderName] = string.Join(", ", header.Take(settings.MaxHeaderKeys).Select(key => CacheKeys.WithPrefix(settings.KeyPrefix, key)));
        IReadOnlyList<string> keys = tags.All;
        http.Items[OutputCacheTagsItem] = keys;

        // Umbraco asks OutputCacheTags for tags once per item it resolved, so a listing with no items
        // (a search nothing matches yet, a type with no published items) would be cached untagged, and
        // no purge could evict it: publishing its first item would leave it empty until it expired.
        // Tagging the output cache entry directly covers every response.
        http.Features.Get<IOutputCacheFeature>()?.Context.Tags.UnionWith(keys);
    }

    /// <summary>
    ///     What a response lists for items too many to list: their stored set, or their types when the
    ///     set is off or could not be stored in time.
    /// </summary>
    private async Task<IEnumerable<string>> OverflowAsync(IReadOnlyList<string> items, bool sets)
        => sets && await keySets.TryStoreAsync(items) is { } set ? [set] : collector.TypeKeys(items);

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
