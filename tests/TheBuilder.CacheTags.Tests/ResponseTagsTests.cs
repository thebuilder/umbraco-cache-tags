using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;

namespace TheBuilder.CacheTags.Tests;

public sealed class ResponseTagsTests
{
    private const string Harbour = "content-e0000000-0000-0000-0000-000000000003";
    private const string Forest = "content-e0000000-0000-0000-0000-000000000004";

    private static IEnumerable<string> Types(IReadOnlyList<string> items) => ["type-article"];

    [Fact]
    public void A_response_carries_umbraco_its_selectors_its_custom_keys_and_its_items_in_that_order()
    {
        IReadOnlyList<string> keys = ResponseTags.For(64, 128, new HashSet<string> { Harbour, "x-label", "search-deliveryapicontentindex", "children-e0000000-0000-0000-0000-000000000002" }, Types);

        Assert.Equal(["umbraco", "children-e0000000-0000-0000-0000-000000000002", "search-deliveryapicontentindex", "x-label", Harbour], keys);
    }

    [Fact]
    public void A_response_showing_more_items_than_it_lists_carries_their_types()
    {
        IReadOnlyList<string> keys = ResponseTags.For(1, 128, new HashSet<string> { Forest, Harbour, "search-deliveryapicontentindex" }, Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", "type-article"], keys);
    }

    [Fact]
    public void Items_that_would_push_the_header_past_its_limit_are_listed_as_their_types()
    {
        var items = Enumerable.Range(1, 127).Select(i => $"content-e0000000-0000-0000-0000-{i:D12}").ToList();

        IReadOnlyList<string> keys = ResponseTags.For(200, 128, new HashSet<string>([.. items, "search-deliveryapicontentindex"]), Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", "type-article"], keys);
    }
}

public sealed class PurgePlanTests
{
    [Fact]
    public void A_plan_purges_its_keys_and_its_selectors_again_once_the_index_has_the_change()
    {
        var plan = new PurgePlan(["content-e0000000-0000-0000-0000-000000000003", "search-deliveryapicontentindex", "type-article"], Everything: false);

        Assert.Equal(["shop-content-e0000000-0000-0000-0000-000000000003", "shop-search-deliveryapicontentindex", "shop-type-article"], plan.Purged("shop-"));
        Assert.Equal(["shop-search-deliveryapicontentindex", "shop-type-article"], plan.Listings("shop-"));
    }

    [Fact]
    public void A_plan_of_everything_purges_umbraco_at_once_and_its_listings_again_later()
    {
        // A renamed content type: every response now, then the listings of both aliases once every
        // index has the rename.
        var plan = new PurgePlan(["type-article", "type-post"], Everything: true);

        Assert.Equal(["umbraco"], plan.Purged(""));
        Assert.Equal(["type-article", "type-post"], plan.Listings(""));
    }
}

public sealed class RequestedItemsTests
{
    private const string Shown = "e0000000-0000-0000-0000-000000000003";
    private const string Missing = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Items_asked_for_by_id_that_all_exist_add_nothing_to_what_the_response_shows()
    {
        var keys = new HashSet<string> { $"content-{Shown}" };

        ResponseCollector.CollectRequestedItems([Shown], media: false, keys);

        Assert.Equal([$"content-{Shown}"], keys);
    }

    [Fact]
    public void Items_listed_as_their_types_keep_their_own_key_when_their_type_cannot_be_read()
    {
        // The missing item is not published yet: its publish plans its content- key, not a type.
        IPublishedContentType article = Fake.Of<IPublishedContentType>((method, _) => method.Name == "get_Alias" ? "article" : null);
        IPublishedContent shown = Fake.Of<IPublishedContent>((method, _) => method.Name == "get_ContentType" ? article : null);
        IPublishedContentCache cache = Fake.Of<IPublishedContentCache>((method, arguments) =>
            method.Name == "GetById" && arguments.Length > 0 && arguments[^1] is Guid id && id == new Guid(Shown) ? shown : null);
        var collector = new ResponseCollector(null!, cache, Fake.Of<IPublishedMediaCache>(), Fake.Of<IVariationContextAccessor>(), null!, null!, null!);

        Assert.Equal([$"content-{Missing}", "type-article"], collector.TypeKeys([$"content-{Shown}", $"content-{Missing}"]));
    }

    [Fact]
    public void One_item_asked_for_twice_is_one_item()
    {
        var keys = new HashSet<string> { $"content-{Shown}" };

        ResponseCollector.CollectRequestedItems([Shown, Shown.ToUpperInvariant()], media: false, keys);

        Assert.Equal([$"content-{Shown}"], keys);
    }

    [Fact]
    public void An_item_asked_for_that_does_not_exist_is_covered_so_its_publish_reaches_the_response()
    {
        var keys = new HashSet<string> { $"content-{Shown}" };

        ResponseCollector.CollectRequestedItems([Shown, Missing, "not-a-guid"], media: false, keys);

        Assert.Equal([$"content-{Missing}", $"content-{Shown}"], keys.Order(StringComparer.Ordinal));
    }
}
