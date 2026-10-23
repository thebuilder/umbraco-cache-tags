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
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 64, 128, Response, new HashSet<string> { Harbour, "children-e0000000-0000-0000-0000-000000000002" }, Types);

        Assert.Equal([Response, "umbraco"], tags.Keys);
        Assert.True(tags.Recorded);
    }

    [Fact]
    public void A_listing_only_the_catch_all_key_selects_carries_its_own_keys_and_is_not_recorded()
    {
        ResponseTags tags = ResponseTags.For(
            CacheKeyMode.ResponseKey,
            64,
            128,
            Response,
            new HashSet<string> { Forest, Harbour, "schema-article", "search-deliveryapicontentindex" },
            Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", Harbour, Forest, "schema-article"], tags.Keys);
        Assert.False(tags.Recorded);
    }

    [Fact]
    public void A_catch_all_listing_showing_more_items_than_the_header_holds_carries_their_types()
    {
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 1, 128, Response, new HashSet<string> { Forest, Harbour, "search-deliveryapicontentindex" }, Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", "type-article"], tags.Keys);
    }

    [Fact]
    public void In_item_keys_mode_a_recorded_response_carries_its_response_key_too()
    {
        ResponseTags tags = ResponseTags.For(CacheKeyMode.ItemKeys, 64, 128, Response, new HashSet<string> { Harbour, "type-article" }, Types);

        Assert.Equal([Response, "umbraco", "type-article", Harbour], tags.Keys);
        Assert.True(tags.Recorded);
    }

    [Fact]
    public void The_keys_purges_need_come_first_and_schema_keys_last()
    {
        // A search page showing 64 items that render 70 different types: 136 keys, more than the 128
        // tags Next.js keeps, which the header cuts from the end.
        var items = Enumerable.Range(1, 64).Select(i => $"content-e0000000-0000-0000-0000-{i:D12}").ToList();
        var schemas = Enumerable.Range(0, 70).Select(i => $"schema-type{i:D2}").ToList();
        var keys = new HashSet<string>([.. items, .. schemas, "search-deliveryapicontentindex"]);

        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 64, 128, Response, keys, Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", .. items, .. schemas], tags.Keys);
    }

    [Fact]
    public void Items_that_would_push_the_header_past_its_limit_are_listed_as_their_types()
    {
        var items = Enumerable.Range(1, 150).Select(i => $"content-e0000000-0000-0000-0000-{i:D12}").ToList();
        var keys = new HashSet<string>([.. items, "schema-article", "search-deliveryapicontentindex"]);

        ResponseTags tags = ResponseTags.For(CacheKeyMode.ResponseKey, 200, 128, Response, keys, Types);

        Assert.Equal(["umbraco", "search-deliveryapicontentindex", "type-article", "schema-article"], tags.Keys);
    }

    [Fact]
    public void In_item_keys_mode_a_recorded_response_lists_the_types_of_items_that_would_not_fit()
    {
        var items = Enumerable.Range(1, 127).Select(i => $"content-e0000000-0000-0000-0000-{i:D12}").ToList();

        ResponseTags tags = ResponseTags.For(CacheKeyMode.ItemKeys, 200, 128, Response, new HashSet<string>(items), Types);

        Assert.Equal([Response, "umbraco", "type-article"], tags.Keys);
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
    public void Several_items_asked_for_make_the_response_a_catch_all_even_when_all_exist()
    {
        const string AlsoShown = "e0000000-0000-0000-0000-000000000004";
        var keys = new HashSet<string> { $"content-{Shown}", $"content-{AlsoShown}" };

        ResponseCollector.CollectRequestedItems([Shown, AlsoShown.ToUpperInvariant(), Shown], media: false, keys);

        Assert.Equal([$"content-{Shown}", $"content-{AlsoShown}", "search-deliveryapicontentindex"], keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void One_item_asked_for_twice_is_one_item()
    {
        var keys = new HashSet<string> { $"content-{Shown}" };

        ResponseCollector.CollectRequestedItems([Shown, Shown.ToUpperInvariant()], media: false, keys);

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
