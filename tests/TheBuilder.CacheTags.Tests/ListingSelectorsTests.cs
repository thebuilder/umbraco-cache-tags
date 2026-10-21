using TheBuilder.CacheTags.Collection;

namespace TheBuilder.CacheTags.Tests;

public sealed class ListingSelectorsTests
{
    private static readonly Guid Articles = new("e0000000-0000-0000-0000-000000000002");

    private static Guid? Resolve(string target) => target == "/articles/" ? Articles : null;

    [Fact]
    public void Children_of_a_path_select_that_parent_and_the_parent_itself()
    {
        var keys = ListingSelectors.For("children:/articles/", [], Resolve, media: false);

        Assert.Equal(["children-e0000000-0000-0000-0000-000000000002", "content-e0000000-0000-0000-0000-000000000002"], keys);
    }

    [Fact]
    public void Children_win_over_a_content_type_filter()
    {
        var keys = ListingSelectors.For("children:/articles/", ["contentType:article"], Resolve, media: false);

        Assert.DoesNotContain("type-article", keys);
        Assert.Contains("children-e0000000-0000-0000-0000-000000000002", keys);
    }

    [Fact]
    public void A_content_type_filter_selects_each_type_lowercased()
    {
        var keys = ListingSelectors.For(null, ["contentType:articlePage,newsItem"], Resolve, media: false);

        Assert.Equal(["type-articlepage", "type-newsitem"], keys);
    }

    [Theory]
    [InlineData("descendants:/articles/", "contentType:!article")]
    [InlineData(null, "name:harbour")]
    [InlineData("children:/missing/", null)]
    public void Anything_else_selects_every_listing_on_the_index(string? fetch, string? filter)
    {
        var keys = ListingSelectors.For(fetch, filter is null ? [] : [filter], Resolve, media: false);

        Assert.Contains("search-deliveryapicontentindex", keys);
    }

    [Fact]
    public void Media_listings_use_media_type_filters_and_the_media_search_key()
    {
        Assert.Equal(["type-image"], ListingSelectors.For(null, ["mediaType:Image"], Resolve, media: true));
        Assert.Equal(["search-media"], ListingSelectors.For(null, [], Resolve, media: true));
    }
}
