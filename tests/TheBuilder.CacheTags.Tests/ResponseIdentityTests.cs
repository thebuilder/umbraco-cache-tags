using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Tests;

public sealed class ResponseIdentityTests
{
    private static KeyValuePair<string, string?> P(string name, string? value) => new(name, value);

    [Fact]
    public void Normalising_sorts_parameters_drops_empty_values_and_defaults_and_encodes_the_rest()
    {
        var query = ResponseIdentity.NormaliseQuery([
            P("take", "10"),
            P("filter", "contentType:article"),
            P("expand", ""),
            P("Fetch", "children:/articles/"),
            P("skip", "0"),
        ]);

        Assert.Equal("fetch=children%3A%2Farticles%2F&filter=contentType%3Aarticle", query);
    }

    [Fact]
    public void Non_default_paging_is_kept()
    {
        Assert.Equal("skip=10&take=5", ResponseIdentity.NormaliseQuery([P("take", "5"), P("skip", "10")]));
    }

    [Fact]
    public void Parameters_are_ordered_by_name_not_by_value()
    {
        Assert.Equal("fetch=children%3A%2Fa%2F&take=5", ResponseIdentity.NormaliseQuery([P("take", "5"), P("fetch", "children:/a/")]));
    }

    [Fact]
    public void Repeated_parameters_keep_every_value_in_a_stable_order()
    {
        Assert.Equal(
            "filter=contentType%3Aarticle&filter=name%3Aharbour",
            ResponseIdentity.NormaliseQuery([P("filter", "name:harbour"), P("filter", "contentType:article")]));
    }

    // Expected values computed outside the package: Python's hashlib.sha256 over the same fields
    // joined by newlines, first 32 hex characters.
    [Fact]
    public void The_key_is_the_truncated_sha256_of_the_identity()
    {
        ResponseIdentity listing = ResponseIdentity.Create(
            "/umbraco/delivery/api/v2/content",
            [P("filter", "contentType:article"), P("fetch", "children:/articles/")],
            culture: "da-DK",
            segment: null,
            startItem: "Site");
        ResponseIdentity item = ResponseIdentity.Create("/umbraco/delivery/api/v2/content/item/Articles/", [], null, null, null);

        Assert.Equal("response-e200fc8d2981f4d13248f90a0384d772", listing.ToKey());
        Assert.Equal("response-b8b882fdaac4c330543d7a6e943423d9", item.ToKey());
    }

    [Fact]
    public void Parameter_order_and_path_case_do_not_change_the_key()
    {
        var first = ResponseIdentity.Create("/umbraco/delivery/api/v2/content", [P("fetch", "children:/a/"), P("take", "5")], "en-US", null, null);
        var second = ResponseIdentity.Create("/Umbraco/Delivery/Api/v2/content", [P("take", "5"), P("fetch", "children:/a/")], "en-us", null, null);

        Assert.Equal(first.ToKey(), second.ToKey());
    }

    [Theory]
    [InlineData("da-DK", "", "")]
    [InlineData("en-US", "summer", "")]
    [InlineData("en-US", "", "other-site")]
    public void Culture_segment_and_start_item_each_change_the_key(string culture, string segment, string startItem)
    {
        var baseline = ResponseIdentity.Create("/umbraco/delivery/api/v2/content/item/", [], "en-US", "", "");
        var varied = ResponseIdentity.Create("/umbraco/delivery/api/v2/content/item/", [], culture, segment, startItem);

        Assert.NotEqual(baseline.ToKey(), varied.ToKey());
    }
}
