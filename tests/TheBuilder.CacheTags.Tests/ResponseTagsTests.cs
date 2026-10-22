using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Tests;

public sealed class ResponseTagsTests
{
    private const string Response = "response-83dcadcd6bbb144ef252035d66d20df5";
    private const string Harbour = "content-e0000000-0000-0000-0000-000000000003";
    private const string Forest = "content-e0000000-0000-0000-0000-000000000004";

    private static IEnumerable<string> Types(IReadOnlyList<string> items) => ["type-article"];

    [Fact]
    public void A_response_carries_its_response_key_and_the_key_of_everything_and_is_recorded()
    {
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 64, Response, new HashSet<string> { Harbour, "children-e0000000-0000-0000-0000-000000000002" }, Types);

        Assert.Equal([Response, "umbraco"], tags.Keys);
        Assert.True(tags.Recorded);
    }

    [Fact]
    public void A_listing_only_the_catch_all_key_selects_carries_its_own_keys_and_is_not_recorded()
    {
        ResponseTags tags = ResponseTags.For(
            CacheKeyMode.ResponseKey,
            64,
            Response,
            new HashSet<string> { Forest, Harbour, "schema-article", "search-deliveryapicontentindex" },
            Types);

        Assert.Equal([Harbour, Forest, "schema-article", "search-deliveryapicontentindex", "umbraco"], tags.Keys);
        Assert.False(tags.Recorded);
    }

    [Fact]
    public void A_catch_all_listing_showing_more_items_than_the_header_holds_carries_their_types()
    {
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 1, Response, new HashSet<string> { Forest, Harbour, "search-deliveryapicontentindex" }, Types);

        Assert.Equal(["type-article", "search-deliveryapicontentindex", "umbraco"], tags.Keys);
    }

    [Fact]
    public void In_item_keys_mode_a_recorded_response_carries_its_response_key_too()
    {
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ItemKeys, 64, Response, new HashSet<string> { Harbour, "type-article" }, Types);

        Assert.Equal([Response, Harbour, "type-article", "umbraco"], tags.Keys);
        Assert.True(tags.Recorded);
    }
}

public sealed class ResolvedPurgeTests
{
    [Fact]
    public void The_listing_purge_names_the_selectors_even_when_no_listing_response_is_recorded()
    {
        var purge = new TheBuilder.CacheTags.Delivery.ResolvedPurge(
            new TheBuilder.CacheTags.Ledger.PurgePlan(["content-e0000000-0000-0000-0000-000000000003", "search-deliveryapicontentindex", "type-article"], false, null),
            Responses: [],
            Listings: []);

        Assert.Equal(["shop-search-deliveryapicontentindex", "shop-type-article"], purge.ListingKeys("shop-"));
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
    public void An_item_asked_for_that_does_not_exist_is_covered_and_makes_the_response_a_catch_all()
    {
        var keys = new HashSet<string> { $"content-{Shown}" };

        ResponseCollector.CollectRequestedItems([Shown, Missing, "not-a-guid"], media: false, keys);

        Assert.Equal([$"content-{Missing}", $"content-{Shown}", "search-deliveryapicontentindex"], keys.Order(StringComparer.Ordinal));
    }
}
