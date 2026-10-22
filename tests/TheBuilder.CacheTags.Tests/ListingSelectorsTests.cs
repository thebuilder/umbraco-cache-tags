using TheBuilder.CacheTags.Collection;

namespace TheBuilder.CacheTags.Tests;

public sealed class ListingSelectorsTests
{
    private static readonly Guid Articles = new("e0000000-0000-0000-0000-000000000002");

    private static Guid? Resolve(string target) => target == "/articles/" ? Articles : null;

    private static bool IsType(string alias) => true;

    [Fact]
    public void Children_of_a_path_select_that_parent_and_the_parent_itself()
    {
        var keys = ListingSelectors.For("children:/articles/", [], Resolve, IsType, media: false);

        Assert.Equal(["children-e0000000-0000-0000-0000-000000000002", "content-e0000000-0000-0000-0000-000000000002"], keys);
    }

    [Fact]
    public void Children_win_over_a_content_type_filter()
    {
        var keys = ListingSelectors.For("children:/articles/", ["contentType:article"], Resolve, IsType, media: false);

        Assert.DoesNotContain("type-article", keys);
        Assert.Contains("children-e0000000-0000-0000-0000-000000000002", keys);
    }

    [Fact]
    public void A_content_type_filter_selects_each_type_lowercased()
    {
        var keys = ListingSelectors.For(null, ["contentType:articlePage,newsItem"], Resolve, IsType, media: false);

        Assert.Equal(["type-articlepage", "type-newsitem"], keys);
    }

    [Theory]
    [InlineData("descendants:/articles/", "contentType:!article")]
    [InlineData(null, "name:harbour")]
    [InlineData("children:/missing/", null)]
    public void Anything_else_selects_every_listing_on_the_index(string? fetch, string? filter)
    {
        var keys = ListingSelectors.For(fetch, filter is null ? [] : [filter], Resolve, IsType, media: false);

        Assert.Contains("search-deliveryapicontentindex", keys);
    }

    [Fact]
    public void Media_listings_use_media_type_filters_and_the_media_search_key()
    {
        Assert.Equal(["type-image"], ListingSelectors.For(null, ["mediaType:Image"], Resolve, IsType, media: true));
        Assert.Equal(["search-media"], ListingSelectors.For(null, [], Resolve, IsType, media: true));
    }

    [Fact]
    public void A_content_type_longer_than_an_alias_can_be_is_not_a_selector()
    {
        var keys = ListingSelectors.For(null, ["contentType:article," + new string('a', 256)], Resolve, IsType, media: false);

        Assert.Equal(["type-article"], keys);
    }

    [Fact]
    public void A_filter_on_anything_but_the_type_makes_a_listing_a_catch_all_whatever_else_selects_it()
    {
        Assert.Equal(
            ["children-e0000000-0000-0000-0000-000000000002", "content-e0000000-0000-0000-0000-000000000002", "search-deliveryapicontentindex"],
            ListingSelectors.For("children:/articles/", ["name:harbour"], Resolve, IsType, media: false));
        Assert.Equal(
            ["search-deliveryapicontentindex", "type-article"],
            ListingSelectors.For(null, ["contentType:article", "search:harbour"], Resolve, IsType, media: false));
    }

    [Theory]
    [InlineData("ancestors:/missing/")]
    [InlineData("descendants:/missing/")]
    public void A_fetch_of_an_item_that_does_not_exist_is_a_catch_all(string fetch)
    {
        Assert.Equal(["search-deliveryapicontentindex"], ListingSelectors.For(fetch, ["contentType:article"], Resolve, IsType, media: false));
    }

    [Fact]
    public void A_type_filter_on_more_types_than_a_listing_is_selected_by_is_a_catch_all()
    {
        var types = string.Join(',', Enumerable.Range(1, 10).Select(i => $"type{i}"));

        Assert.Equal(10, ListingSelectors.For(null, ["contentType:" + types], Resolve, IsType, media: false).Count);
        Assert.Equal(["search-deliveryapicontentindex"], ListingSelectors.For(null, ["contentType:" + types + ",type11"], Resolve, IsType, media: false));
        Assert.Equal(["search-deliveryapicontentindex"], ListingSelectors.For(null, ["contentType:" + types, "contentType:type11"], Resolve, IsType, media: false));
    }
}
