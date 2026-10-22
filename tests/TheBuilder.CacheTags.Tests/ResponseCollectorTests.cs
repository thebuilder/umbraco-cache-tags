using Microsoft.AspNetCore.Http;
using TheBuilder.CacheTags.Collection;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Tests;

/// <summary>A Delivery API content query (listing), as the collector sees it.</summary>
public sealed class ResponseCollectorTests
{
    private static readonly Guid Articles = new("e0000000-0000-0000-0000-000000000002");
    private static readonly DeliveryEndpoint Query = new(Media: false, Listing: true);

    [Fact]
    public void Children_of_an_id_that_exists_select_that_parent()
    {
        Assert.Equal(
            ["children-e0000000-0000-0000-0000-000000000002", "content-e0000000-0000-0000-0000-000000000002"],
            Collect("?fetch=children:e0000000-0000-0000-0000-000000000002"));
    }

    [Theory]
    [InlineData("?fetch=children:3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("?fetch=descendants:3f2504e0-4f89-11d3-9a0c-0305e82c3301&filter=contentType:article")]
    public void A_fetch_of_an_id_that_names_nothing_is_a_catch_all(string query)
    {
        Assert.Equal(["search-deliveryapicontentindex"], Collect(query));
    }

    [Fact]
    public void A_type_filter_on_types_that_exist_selects_them()
    {
        Assert.Equal(["type-article", "type-page"], Collect("?filter=contentType:article,page"));
    }

    [Theory]
    [InlineData("?filter=contentType:a8f3e1c2")]
    [InlineData("?filter=contentType:article,a8f3e1c2")]
    public void A_type_filter_naming_a_type_that_does_not_exist_is_a_catch_all(string query)
    {
        Assert.Equal(["search-deliveryapicontentindex"], Collect(query));
    }

    private static string[] Collect(string query)
    {
        var collector = new ResponseCollector(
            null!,
            Fake<IPublishedContentCache>.Of((method, args) => method.Name == "GetById" && args[0] is Guid key && key == Articles ? Item(key) : null),
            Fake<IPublishedMediaCache>.Of(),
            Fake<IVariationContextAccessor>.Of(),
            Fake<IApiContentPathResolver>.Of(),
            Fake<IApiMediaQueryService>.Of(),
            Fake<IContentTypeService>.Of((method, args) => method.Name == "Get" && args[0] is "article" or "page" ? Fake<IContentType>.Of() : null),
            Fake<IMediaTypeService>.Of());

        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return collector.Collect(null, http, Query).Order(StringComparer.Ordinal).ToArray();
    }

    private static IPublishedContent Item(Guid key) => Fake<IPublishedContent>.Of((method, _) => method.Name == "get_Key" ? key : null);
}
