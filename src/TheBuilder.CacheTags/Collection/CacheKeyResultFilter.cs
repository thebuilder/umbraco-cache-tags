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
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Runs after every Delivery API action: works out the response's key and what it covers, puts the
///     key in the response header and queues the mapping write. Collecting from the published source
///     values and the result objects, not from Umbraco's builders, is what keeps it complete on warm
///     requests (spike/FINDINGS.md, section 1).
/// </summary>
internal sealed class CacheKeyResultFilter : IAsyncResultFilter
{
    // Umbraco.Cms.Api.Delivery.Caching.DeliveryApiOutputCacheKeys is internal; these are its values
    // from 17.4.0 on. When they are missing, the filter falls back to the items in the result.
    private const string ResolvedContentItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedContentItems";
    private const string ResolvedMediaItemsKey = "Umbraco.DeliveryApi.OutputCache.ResolvedMediaItems";

    /// <summary>Where the response key is kept for the rest of the request.</summary>
    public const string ResponseKeyItem = "TheBuilder.CacheTags.ResponseKey";

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action || IsDeliveryApi(action) is false)
        {
            await next();
            return;
        }

        HttpContext http = context.HttpContext;
        IServiceProvider services = http.RequestServices;
        CacheTagsOptions options = services.GetRequiredService<IOptionsMonitor<CacheTagsOptions>>().CurrentValue;
        if (options.Enabled is false)
        {
            await next();
            return;
        }

        if (services.GetRequiredService<IRequestPreviewService>().IsPreview())
        {
            // Drafts must never be cached by the frontend or a CDN.
            http.Response.Headers[HeaderNames.CacheControl] = "no-store";
            await next();
            return;
        }

        if (http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // A member's response: the key does not vary by member, so it must not be shared.
            http.Response.Headers[HeaderNames.CacheControl] = "private";
            await next();
            return;
        }

        if (context.Result is not ObjectResult { Value: { } value } result || (result.StatusCode ?? 200) != 200)
        {
            await next();
            return;
        }

        var media = action.ControllerTypeInfo.Name.Contains("Media", StringComparison.Ordinal);
        var listing = action.ControllerTypeInfo.Name.StartsWith("Query", StringComparison.Ordinal);
        ResponseIdentity identity = ResponseIdentity.Create(
            http.Request.Path.Value ?? string.Empty,
            http.Request.Query.SelectMany(pair => pair.Value.Select(v => new KeyValuePair<string, string?>(pair.Key, v))),
            services.GetRequiredService<IRequestCultureService>().GetRequestedCulture(),
            services.GetRequiredService<IRequestSegmentService>().GetRequestedSegment(),
            services.GetRequiredService<IRequestStartItemProvider>().RequestedStartItem());
        var responseKey = identity.ToKey();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        OutputWalker.Collect(value, keys);
        CollectReferences(http, services, keys);
        if (listing)
        {
            keys.UnionWith(ListingSelectors.For(
                http.Request.Query["fetch"].ToString(),
                http.Request.Query["filter"].Where(f => f is not null).Select(f => f!),
                target => ResolveTarget(services, target, media),
                media));
        }

        http.Items[ResponseKeyItem] = responseKey;
        http.Response.Headers[Constants.HeaderName] = HeaderValue(options, responseKey, keys, services);

        // The key hashes the culture as requested; the mapping stores the culture actually served
        // (Accept-Language: da resolves to da-DK, no header to the default), which a culture-limited
        // purge compares against.
        var servedCulture = services.GetRequiredService<IVariationContextAccessor>().VariationContext?.Culture?.ToLowerInvariant() ?? identity.Culture;
        DateTime now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        services.GetRequiredService<MappingWriter>().Record(new CachedResponse(
            responseKey,
            identity.Path,
            identity.Query,
            servedCulture,
            identity.Segment,
            identity.StartItem,
            keys.Order(StringComparer.Ordinal).ToArray(),
            now,
            now));

        await next();
    }

    private static bool IsDeliveryApi(ControllerActionDescriptor action)
        => action.ControllerTypeInfo.Namespace?.StartsWith("Umbraco.Cms.Api.Delivery.Controllers", StringComparison.Ordinal) is true;

    // The resolved items' references, read from published source values in the request culture, and
    // one level further for the properties the request expanded.
    private static void CollectReferences(HttpContext http, IServiceProvider services, HashSet<string> keys)
    {
        var resolved = new List<IPublishedContent>();
        if (http.Items[ResolvedContentItemsKey] is IPublishedContent[] content)
        {
            resolved.AddRange(content);
        }

        if (http.Items[ResolvedMediaItemsKey] is IPublishedContent[] mediaItems)
        {
            resolved.AddRange(mediaItems);
        }

        if (resolved.Count == 0)
        {
            return;
        }

        ReferenceReader reader = services.GetRequiredService<ReferenceReader>();
        var culture = services.GetRequiredService<IVariationContextAccessor>().VariationContext?.Culture;
        Expansion expansion = Expansion.Parse(http.Request.Query["expand"].ToString());
        IPublishedContentCache contentCache = services.GetRequiredService<IPublishedContentCache>();

        foreach (IPublishedContent item in resolved)
        {
            keys.Add(item.ItemType == PublishedItemType.Media ? CacheKeys.Media(item.Key) : CacheKeys.Content(item.Key));
            foreach (ReferencedKey reference in reader.Read(item, culture).ToList())
            {
                keys.Add(reference.Key);
                if (reference.IsMedia || expansion.Expands(reference.PropertyAlias) is false)
                {
                    continue;
                }

                // An expanded picker renders the picked item's properties, and so what they reference.
                if (contentCache.GetById(reference.Id) is { } expanded)
                {
                    keys.UnionWith(reader.Read(expanded, culture).Select(r => r.Key));
                }
            }
        }
    }

    private static Guid? ResolveTarget(IServiceProvider services, string target, bool media)
    {
        if (Guid.TryParse(target, out Guid key))
        {
            return key;
        }

        // "children:/" lists the children of the item routed at "/" (the site's root page). Only when
        // nothing is routed there does it mean the root level itself.
        IPublishedContent? item = media
            ? services.GetRequiredService<IApiMediaQueryService>().GetByPath(target)
            : services.GetRequiredService<IApiContentPathResolver>().ResolveContentPath(target);
        return item?.Key ?? (target is "/" or "" ? Guid.Empty : null);
    }

    private static string HeaderValue(CacheTagsOptions options, string responseKey, HashSet<string> keys, IServiceProvider services)
    {
        if (options.Mode == CacheKeyMode.Response)
        {
            return CacheKeys.WithPrefix(options.KeyPrefix, responseKey);
        }

        // Direct mode: the item keys themselves, then the listing selectors.
        var items = keys.Where(CacheKeys.IsItem).Order(StringComparer.Ordinal).ToList();
        IEnumerable<string> header = items.Count <= options.DirectKeyBudget ? items : TypeKeys(items, services);
        return string.Join(", ", new[] { responseKey }
            .Concat(header)
            .Concat(keys.Where(CacheKeys.IsSelector).Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(key => CacheKeys.WithPrefix(options.KeyPrefix, key)));
    }

    // Over budget: the content and media types of every item, which a publish of any of them purges.
    private static IEnumerable<string> TypeKeys(IEnumerable<string> itemKeys, IServiceProvider services)
    {
        IPublishedContentCache contentCache = services.GetRequiredService<IPublishedContentCache>();
        IPublishedMediaCache mediaCache = services.GetRequiredService<IPublishedMediaCache>();
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in itemKeys)
        {
            IPublishedContent? item = key.StartsWith(CacheKeys.MediaPrefix, StringComparison.Ordinal)
                ? mediaCache.GetById(Guid.Parse(key[CacheKeys.MediaPrefix.Length..]))
                : contentCache.GetById(Guid.Parse(key[CacheKeys.ContentPrefix.Length..]));
            types.Add(item is null ? CacheKeys.All : CacheKeys.Type(item.ContentType.Alias));
        }

        return types;
    }
}
