using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Umbraco.Cms.Api.Delivery.Controllers.Content;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Tests;

public sealed class CacheKeyResultFilterTests
{
    private static readonly CacheTagsOptions Settings = new();

    // An item in a response, as the Delivery API returns it.
    private static IApiContent Shown(string id) => Fake.Of<IApiContent>((method, _) => method.Name switch
    {
        "get_Id" => new Guid(id),
        "get_ContentType" => "article",
        "get_Properties" => new Dictionary<string, object?>(),
        _ => null,
    });

    [Fact]
    public async Task A_listing_carries_umbraco_its_selector_and_the_items_it_shows()
    {
        const string A = "e0000000-0000-0000-0000-000000000003", B = "e0000000-0000-0000-0000-000000000004";

        HttpContext http = await RunAsync(new Request(typeof(QueryContentApiController), "/umbraco/delivery/api/v2/content", "?filter=contentType:article", Value: new[] { Shown(A), Shown(B) }));

        Assert.Equal(["umbraco", "type-article", $"content-{A}", $"content-{B}"], http.Response.Headers[Settings.HeaderName].ToString().Split(", "));
    }

    [Fact]
    public async Task A_header_stops_at_the_tag_limit_but_Umbracos_output_cache_keeps_every_key()
    {
        var named = Enumerable.Range(0, 200).Select(i => $"x-label-{i:D3}").ToList();

        HttpContext http = await RunAsync(new Request(typeof(DictionaryController), "/api/labels", Named: named));

        var header = http.Response.Headers[Settings.HeaderName].ToString().Split(", ");
        Assert.Equal(128, header.Length);
        Assert.Equal("umbraco", header[0]);
        Assert.Equal(["umbraco", .. named], (IReadOnlyList<string>)http.Items[CacheKeyResultFilter.OutputCacheTagsItem]!);
    }

    [Fact]
    public async Task The_header_stops_at_the_configured_limit()
    {
        var named = Enumerable.Range(0, 20).Select(i => $"x-label-{i:D2}").ToList();

        HttpContext http = await RunAsync(new Request(typeof(DictionaryController), "/api/labels", Named: named, Options: new CacheTagsOptions { MaxHeaderKeys = 10 }));

        Assert.Equal(["umbraco", .. named.Take(9)], http.Response.Headers[Settings.HeaderName].ToString().Split(", "));
        Assert.Equal(["umbraco", .. named], (IReadOnlyList<string>)http.Items[CacheKeyResultFilter.OutputCacheTagsItem]!);
    }

    [Fact]
    public async Task An_empty_listing_is_tagged_in_Umbracos_output_cache_so_its_first_item_evicts_it()
    {
        // Umbraco asks for output cache tags once per item it resolved, so it never asks for these.
        var outputCache = new OutputCacheFeature(new OutputCacheContext { HttpContext = new DefaultHttpContext() });

        await RunAsync(new Request(typeof(QueryContentApiController), "/umbraco/delivery/api/v2/content", "?filter=search:harbour", Value: Array.Empty<IApiContent>(), OutputCache: outputCache));

        Assert.Equal(["search-deliveryapicontentindex", "umbraco"], outputCache.Context.Tags.Order(StringComparer.Ordinal));
    }

    private sealed record OutputCacheFeature(OutputCacheContext Context) : IOutputCacheFeature;

    private sealed record Request(Type Controller, string Path, string Query = "", object? Value = null, IReadOnlyList<string>? Named = null, CacheTagsOptions? Options = null, IOutputCacheFeature? OutputCache = null);

    /// <summary>Runs the filter over a 200 response to the request.</summary>
    private static async Task<HttpContext> RunAsync(Request request)
    {
        var named = new CacheTagsResponse();
        foreach (var key in request.Named ?? ["x-dictionary-en"])
        {
            named.Add(key);
        }
        KnownNames names = Names.Of(["article"]);
        IVariationContextAccessor variation = Fake.Of<IVariationContextAccessor>();
        var filter = new CacheKeyResultFilter(
            new TestOptions(request.Options ?? Settings),
            Fake.Of<IRequestPreviewService>(),
            new ResponseCollector(
                null!,
                Fake.Of<IPublishedContentCache>(),
                Fake.Of<IPublishedMediaCache>(),
                variation,
                Fake.Of<IApiContentPathResolver>(),
                Fake.Of<IApiMediaQueryService>(),
                names),
            named);

        var http = new DefaultHttpContext();
        http.Request.Path = request.Path;
        http.Request.QueryString = new QueryString(request.Query);
        if (request.OutputCache is not null)
        {
            http.Features.Set(request.OutputCache);
        }
        var action = new ControllerActionDescriptor
        {
            ControllerTypeInfo = request.Controller.GetTypeInfo(),
            MethodInfo = typeof(DictionaryController).GetMethod(nameof(DictionaryController.Get))!,
        };
        var context = new ResultExecutingContext(
            new ActionContext(http, new RouteData(), action),
            [],
            new ObjectResult(request.Value ?? new { tagline = "Hello" }),
            new DictionaryController());

        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, context.Controller)));

        return http;
    }

    [CacheTagged]
    private sealed class DictionaryController : ControllerBase
    {
        public IActionResult Get() => Ok();
    }
}
