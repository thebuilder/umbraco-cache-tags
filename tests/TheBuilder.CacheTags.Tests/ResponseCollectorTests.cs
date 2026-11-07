using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Collection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.Editors;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Delivery API content responses, as the collector sees them.</summary>
public sealed class ResponseCollectorTests
{
    private static readonly Guid Articles = new("e0000000-0000-0000-0000-000000000002");
    private static readonly DeliveryEndpoint Query = new(Media: false, Listing: true);
    private static readonly Guid Home = new("e0000000-0000-0000-0000-000000000010");
    internal static readonly Guid Featured = new("e0000000-0000-0000-0000-000000000011");
    private static readonly Guid Deep = new("e0000000-0000-0000-0000-000000000012");
    internal static readonly Guid Unexpanded = new("e0000000-0000-0000-0000-000000000013");
    private static readonly Guid Linked = new("e0000000-0000-0000-0000-000000000014");
    private static readonly Guid FeaturedImage = new("f0000000-0000-0000-0000-000000000011");
    private static readonly Guid DeepImage = new("f0000000-0000-0000-0000-000000000012");
    private static readonly Guid UnexpandedImage = new("f0000000-0000-0000-0000-000000000013");
    private static readonly Guid LinkedImage = new("f0000000-0000-0000-0000-000000000014");

    private static readonly IApiContentRoute Route = new ApiContentRoute("/x/", new ApiContentStartItem(Guid.Empty, "site"));

    // What each published item's source values reference, as Umbraco's reference factories read them.
    private static readonly Dictionary<Guid, Udi[]> Sources = new()
    {
        [Home] = [Doc(Featured), Doc(Unexpanded), Doc(Linked)],
        [Featured] = [Doc(Deep), Img(FeaturedImage)],
        [Deep] = [Img(DeepImage)],
        [Unexpanded] = [Img(UnexpandedImage)],
        [Linked] = [Img(LinkedImage)],
    };

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

    [Fact]
    public void A_type_filter_with_an_alias_longer_than_umbraco_allows_is_a_catch_all()
    {
        Assert.Equal(["search-deliveryapicontentindex"], Collect("?filter=contentType:article," + new string('a', 256)));
    }

    [Fact]
    public void An_item_rendered_with_its_properties_brings_its_references_and_a_linked_or_unexpanded_one_does_not()
    {
        // Home expands its featured pick two levels deep, picks an item it doesn't expand, and links one.
        ApiContent result = Content(Home, new Dictionary<string, object?>
        {
            ["featured"] = Content(Featured, new Dictionary<string, object?> { ["related"] = Content(Deep, new Dictionary<string, object?> { ["title"] = "Deep" }) }),
            ["more"] = Content(Unexpanded),
            ["navigation"] = new[] { ApiLink.Content("Linked", null, null, Linked, "page", Route, null) },
        });

        Assert.Equal(
            [
                "content-e0000000-0000-0000-0000-000000000010",
                "content-e0000000-0000-0000-0000-000000000011",
                "content-e0000000-0000-0000-0000-000000000012",
                "content-e0000000-0000-0000-0000-000000000013",
                "content-e0000000-0000-0000-0000-000000000014",
                "media-f0000000-0000-0000-0000-000000000011",
                "media-f0000000-0000-0000-0000-000000000012",
            ],
            CollectItem(result));
    }

    [Fact]
    public void An_expanded_item_showing_some_of_its_properties_brings_all_its_references()
    {
        // fields=properties[featured[properties[title]]]: the response shows the title only, but a
        // change to what the other properties reference still purges it. Too many keys, never too few.
        ApiContent result = Content(Home, new Dictionary<string, object?>
        {
            ["featured"] = Content(Featured, new Dictionary<string, object?> { ["title"] = "Featured" }),
        });

        Assert.Equal(
            [
                "content-e0000000-0000-0000-0000-000000000010",
                "content-e0000000-0000-0000-0000-000000000011",
                "content-e0000000-0000-0000-0000-000000000012",
                "content-e0000000-0000-0000-0000-000000000013",
                "content-e0000000-0000-0000-0000-000000000014",
                "media-f0000000-0000-0000-0000-000000000011",
            ],
            CollectItem(result));
    }

    private static string[] CollectItem(ApiContent result)
    {
        ResponseCollector collector = Collector();

        // Umbraco records the item the request resolved; the result is what it renders.
        var http = new DefaultHttpContext();
        http.Items["Umbraco.DeliveryApi.OutputCache.ResolvedContentItems"] = new[] { Published(Home) };
        return collector.Collect(result, http, new DeliveryEndpoint(Media: false, Listing: false)).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    ///     A collector over the published items in <see cref="Sources" />, whose values reference what
    ///     <see cref="Sources" /> says, through Umbraco's own reference factory collection.
    /// </summary>
    internal static ResponseCollector Collector()
    {
        IDataEditor editor = Fake.Of<IDataEditor>((method, _) => method.Name == "get_Alias" ? "refs" : null);
        IDataValueReference values = Fake.Of<IDataValueReference>((method, args) => method.Name == nameof(IDataValueReference.GetReferences)
            ? ((Udi[])args[0]!).Select(udi => new UmbracoEntityReference(udi)).ToArray()
            : null);
        IDataValueReferenceFactory factory = Fake.Of<IDataValueReferenceFactory>((method, _) => method.Name switch
        {
            nameof(IDataValueReferenceFactory.IsForEditor) => true,
            nameof(IDataValueReferenceFactory.GetDataValueReference) => values,
            _ => null,
        });
        var reader = new ReferenceReader(
            new PropertyEditorCollection(new DataEditorCollection(() => [editor])),
            new DataValueReferenceFactoryCollection(() => [factory], NullLogger<DataValueReferenceFactoryCollection>.Instance));

        return new ResponseCollector(
            reader,
            Fake.Of<IPublishedContentCache>((method, args) => method.Name == "GetById" && args[0] is Guid key && Sources.ContainsKey(key) ? Published(key) : null),
            Fake.Of<IPublishedMediaCache>(),
            Fake.Of<IVariationContextAccessor>(),
            Fake.Of<IApiContentPathResolver>(),
            Fake.Of<IApiMediaQueryService>(),
            Names.Of(["page"]));
    }

    private static IPublishedContent Published(Guid key)
    {
        IPublishedPropertyType type = Fake.Of<IPublishedPropertyType>((method, _) => method.Name == "get_EditorAlias" ? "refs" : null);
        IPublishedProperty property = Fake.Of<IPublishedProperty>((method, _) => method.Name switch
        {
            "get_PropertyType" => type,
            nameof(IPublishedProperty.GetSourceValue) => Sources[key],
            _ => null,
        });
        return Fake.Of<IPublishedContent>((method, _) => method.Name switch
        {
            "get_Key" => key,
            "get_Properties" => new[] { property },
            _ => null,
        });
    }

    internal static ApiContent Content(Guid id, Dictionary<string, object?>? properties = null)
        => new(id, "name", "page", DateTime.UnixEpoch, DateTime.UnixEpoch, Route, properties ?? []);

    private static GuidUdi Doc(Guid id) => new(Umbraco.Cms.Core.Constants.UdiEntityType.Document, id);

    private static GuidUdi Img(Guid id) => new(Umbraco.Cms.Core.Constants.UdiEntityType.Media, id);

    private static string[] Collect(string query)
    {
        var collector = new ResponseCollector(
            null!,
            Fake.Of<IPublishedContentCache>((method, args) => method.Name == "GetById" && args[0] is Guid key && key == Articles ? Item(key) : null),
            Fake.Of<IPublishedMediaCache>(),
            Fake.Of<IVariationContextAccessor>(),
            Fake.Of<IApiContentPathResolver>(),
            Fake.Of<IApiMediaQueryService>(),
            Names.Of(["article", "page"]));

        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return collector.Collect(null, http, Query).Order(StringComparer.Ordinal).ToArray();
    }

    private static IPublishedContent Item(Guid key) => Fake.Of<IPublishedContent>((method, _) => method.Name == "get_Key" ? key : null);
}
