using TheBuilder.CacheTags.Keys;

namespace TheBuilder.CacheTags.Tests;

public sealed class ResponseIdentityTests
{
    private static KeyValuePair<string, string?> P(string name, string? value) => new(name, value);

    [Fact]
    public void Normalising_keeps_what_selects_content_sorted_and_encoded_and_drops_empty_values()
    {
        var query = ResponseIdentity.NormaliseQuery([
            P("take", "10"),
            P("filter", "contentType:article"),
            P("expand", ""),
            P("Fetch", "children:/articles/"),
            P("skip", "0"),
        ]);

        Assert.Equal("fetch=children%3A%2Farticles%2F&filter=contenttype%3Aarticle", query);
    }

    [Fact]
    public void Paging_sorting_and_presentation_do_not_change_the_key()
    {
        Assert.Equal(
            "fetch=children%3A%2Farticles%2F",
            ResponseIdentity.NormaliseQuery([
                P("fetch", "children:/articles/"),
                P("take", "5"),
                P("skip", "500"),
                P("sort", "name:desc"),
                P("expand", "properties[$all]"),
                P("fields", "properties[random]"),
            ]));
    }

    [Fact]
    public void Parameters_are_ordered_by_name_not_by_value()
    {
        Assert.Equal(
            "fetch=children%3A%2Fa%2F&filter=contenttype%3Aarticle",
            ResponseIdentity.NormaliseQuery([P("filter", "contentType:article"), P("fetch", "children:/a/")]));
    }

    [Fact]
    public void Repeated_parameters_keep_every_value_in_a_stable_order()
    {
        Assert.Equal(
            "filter=contenttype%3Aarticle&filter=name%3Aharbour",
            ResponseIdentity.NormaliseQuery([P("filter", "name:harbour"), P("filter", "contentType:article")]));
    }

    [Fact]
    public void Values_are_lowercased_and_ids_written_in_one_form()
    {
        Assert.Equal(
            "fetch=children%3A3f2504e0-4f89-11d3-9a0c-0305e82c3301&filter=contenttype%3Aarticle&id=3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            ResponseIdentity.NormaliseQuery([
                P("id", "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}"),
                P("fetch", "Children:3F2504E04F8911D39A0C0305E82C3301"),
                P("filter", "contentType:Article"),
            ]));
    }

    [Fact]
    public void Repeated_values_count_once()
    {
        Assert.Equal(
            "fetch=children%3A%2Farticles%2F&id=3f2504e0-4f89-11d3-9a0c-0305e82c3301",
            ResponseIdentity.NormaliseQuery([
                P("id", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
                P("id", "3F2504E0-4F89-11D3-9A0C-0305E82C3301"),
                P("fetch", "children:/articles/"),
                P("fetch", "children:/Articles/"),
            ]));
    }

    [Fact]
    public void Padding_and_the_order_of_a_type_list_do_not_change_the_key()
    {
        Assert.Equal(
            "fetch=children%3A%2Farticles%2F&filter=contenttype%3Aarticle%2Cpage",
            ResponseIdentity.NormaliseQuery([
                P("fetch", " children:/articles/   "),
                P("filter", "contentType: page , article,page"),
            ]));
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

        Assert.Equal("response-0ac652de573895ae257a6c3efcdc0518", listing.ToKey());
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

    [Fact]
    public void Parameters_the_delivery_api_does_not_read_do_not_change_the_key()
    {
        Assert.Equal("id=e0000000-0000-0000-0000-000000000003", ResponseIdentity.NormaliseQuery([P("id", "e0000000-0000-0000-0000-000000000003"), P("utm_source", "mail"), P("x", "8f2c")]));
    }

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(101, 0, false)]
    [InlineData(0, 201, false)]
    public void A_segment_or_start_item_longer_than_the_mapping_holds_does_not_fit(int segment, int startItem, bool fits)
    {
        ResponseIdentity identity = ResponseIdentity.Create("/umbraco/delivery/api/v2/content", [], null, new string('s', segment), new string('i', startItem));

        Assert.Equal(fits, identity.Fits);
    }
}
