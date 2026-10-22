using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Umbraco.Cms.Api.Delivery.Controllers.Content;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Tests;

public sealed class CacheKeyResultFilterTests
{
    private static readonly CacheTagsOptions Settings = new();

    // The start items the site has, by the Start-Item values that name them (path or id).
    private static readonly Dictionary<string, Guid> StartItems = new()
    {
        ["home"] = new Guid("e0000000-0000-0000-0000-000000000001"),
        ["e0000000-0000-0000-0000-000000000001"] = new Guid("e0000000-0000-0000-0000-000000000001"),
        ["shop"] = new Guid("e0000000-0000-0000-0000-000000000009"),
    };

    // An item in a response, as the Delivery API returns it.
    private static IApiContent Shown(string id) => Fake<IApiContent>.Of((method, _) => method.Name switch
    {
        "get_Id" => new Guid(id),
        "get_ContentType" => "article",
        "get_Properties" => new Dictionary<string, object?>(),
        _ => null,
    });

    private static IPublishedContent Item(Guid key) => Fake<IPublishedContent>.Of((method, _) => method.Name == "get_Key" ? key : null);

    [Fact]
    public async Task A_response_whose_record_is_queued_carries_its_response_key()
    {
        using MappingWriter writer = Writers.Create(new NoStore());

        var keys = await HeaderAsync(writer, new Request(typeof(DictionaryController), "/api/dictionary"));

        Assert.Equal(2, keys.Length);
        Assert.StartsWith("response-", keys[0], StringComparison.Ordinal);
        Assert.Equal("umbraco", keys[1]);
    }

    [Fact]
    public async Task A_response_whose_record_does_not_fit_the_queue_carries_its_keys_itself()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        for (var i = 0; i < MappingWriter.Capacity; i++)
        {
            writer.Record(Writers.Response($"response-{i}"));
        }

        Assert.Equal(["x-dictionary-en", "umbraco"], await HeaderAsync(writer, new Request(typeof(DictionaryController), "/api/dictionary")));
    }

    [Fact]
    public async Task A_response_before_the_package_is_installed_carries_its_keys_itself()
    {
        using MappingWriter writer = Writers.Create(new NoStore(), installed: false);

        Assert.Equal(["x-dictionary-en", "umbraco"], await HeaderAsync(writer, new Request(typeof(DictionaryController), "/api/dictionary")));
    }

    [Fact]
    public async Task An_item_request_by_route_keys_by_none_of_the_query_since_it_reads_none()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Item = "/umbraco/delivery/api/v2/content/item/articles/";

        var plain = await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item));

        Assert.StartsWith("response-", plain[0], StringComparison.Ordinal);
        Assert.Equal(plain, await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, "?id=e0000000-0000-0000-0000-000000000002&filter=a8f3e1c2&fetch=children:/b7d2/")));
    }

    [Fact]
    public async Task An_items_request_keys_by_one_id_and_is_a_catch_all_for_several()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Items = "/umbraco/delivery/api/v2/content/items";
        const string A = "e0000000-0000-0000-0000-000000000003", B = "e0000000-0000-0000-0000-000000000004";
        object[] both = [Shown(A), Shown(B)];

        var a = await HeaderAsync(writer, new Request(typeof(ByIdsContentApiController), Items, $"?id={A}", Value: both));

        Assert.StartsWith("response-", a[0], StringComparison.Ordinal);
        Assert.Equal(a, await HeaderAsync(writer, new Request(typeof(ByIdsContentApiController), Items, $"?id={A}&filter=a8f3e1c2&fetch=children:/b7d2/", Value: both)));
        Assert.NotEqual(a, await HeaderAsync(writer, new Request(typeof(ByIdsContentApiController), Items, $"?id={B}", Value: both)));
        Assert.DoesNotContain(
            await HeaderAsync(writer, new Request(typeof(ByIdsContentApiController), Items, $"?id={A}&id={B}", Value: both)),
            key => key.StartsWith("response-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_listing_request_keys_by_fetch_and_filter_only_since_a_query_endpoint_ignores_id()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Listing = "/umbraco/delivery/api/v2/content";

        var plain = await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article"));

        Assert.StartsWith("response-", plain[0], StringComparison.Ordinal);
        Assert.Equal(plain, await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article&id=3f2504e0-4f89-11d3-9a0c-0305e82c3301")));
        Assert.NotEqual(plain, await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article&fetch=children:/")));
    }

    [Fact]
    public async Task A_culture_that_is_no_language_is_not_recorded()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Listing = "/umbraco/delivery/api/v2/content";

        var english = await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article", Culture: "en-US"));
        var danish = await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article", Culture: "da-dk"));

        Assert.StartsWith("response-", english[0], StringComparison.Ordinal);
        Assert.StartsWith("response-", danish[0], StringComparison.Ordinal);
        Assert.NotEqual(english, danish);
        Assert.Equal(["type-article", "umbraco"], await HeaderAsync(writer, new Request(typeof(QueryContentApiController), Listing, "?filter=contentType:article", Culture: "x8f3e1c2")));
    }

    [Fact]
    public async Task Segments_share_a_key()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Item = "/umbraco/delivery/api/v2/content/item/articles/";

        var plain = await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, Culture: "en-us"));

        Assert.StartsWith("response-", plain[0], StringComparison.Ordinal);
        Assert.Equal(plain, await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, Culture: "en-us", Segment: "x8f3e1c2")));
    }

    [Fact]
    public async Task A_start_item_is_keyed_as_the_item_it_resolves_to()
    {
        using MappingWriter writer = Writers.Create(new NoStore());
        const string Item = "/umbraco/delivery/api/v2/content/item/articles/";

        var home = await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, Culture: "en-us", StartItem: "home"));

        Assert.StartsWith("response-", home[0], StringComparison.Ordinal);
        Assert.Equal(home, await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, Culture: "en-us", StartItem: "e0000000-0000-0000-0000-000000000001")));
        Assert.NotEqual(home, await HeaderAsync(writer, new Request(typeof(ByRouteContentApiController), Item, Culture: "en-us", StartItem: "shop")));
    }

    private sealed record Request(Type Controller, string Path, string Query = "", string? Culture = null, string? Segment = null, string? StartItem = null, object? Value = null);

    /// <summary>Runs the filter over a 200 response to the request, and returns its header's keys.</summary>
    private static async Task<string[]> HeaderAsync(MappingWriter writer, Request request)
    {
        var named = new CacheTagsResponse();
        named.Add("x-dictionary-en");
        KnownNames names = Names.Of(["article"], cultures: ["en-US", "da-DK"]);
        IVariationContextAccessor variation = Fake<IVariationContextAccessor>.Of((method, _) => method.Name == "get_VariationContext" && request.Culture is not null ? new VariationContext(request.Culture, request.Segment) : null);
        var filter = new CacheKeyResultFilter(
            Writers.Options(Settings),
            Fake<IRequestPreviewService>.Of(),
            Fake<IRequestCultureService>.Of((method, _) => method.Name == "GetRequestedCulture" ? request.Culture : null),
            Fake<IRequestStartItemProvider>.Of((method, _) => method.Name switch
            {
                "RequestedStartItem" => request.StartItem,
                "GetStartItem" => StartItems.TryGetValue(request.StartItem ?? string.Empty, out Guid key) ? Item(key) : null,
                _ => null,
            }),
            variation,
            new ResponseCollector(
                null!,
                Fake<IPublishedContentCache>.Of(),
                Fake<IPublishedMediaCache>.Of(),
                variation,
                Fake<IApiContentPathResolver>.Of(),
                Fake<IApiMediaQueryService>.Of(),
                names),
            names,
            named,
            writer,
            TimeProvider.System);

        var http = new DefaultHttpContext();
        http.Request.Path = request.Path;
        http.Request.QueryString = new QueryString(request.Query);
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

        return http.Response.Headers[Settings.HeaderName].ToString().Split(", ");
    }

    [CacheTagged]
    private sealed class DictionaryController : ControllerBase
    {
        public IActionResult Get() => Ok();
    }

    private sealed class NoStore : ICacheTagStore
    {
        public Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default) => Task.FromResult<CachedResponse?>(null);

        public Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
