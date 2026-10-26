using TheBuilder.CacheTags.Collection;
using Umbraco.Cms.Api.Common.ViewModels.Pagination;
using Umbraco.Cms.Core.Models.DeliveryApi;

namespace TheBuilder.CacheTags.Tests;

public sealed class OutputWalkerTests
{
    private static readonly Guid Page = new("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Picked = new("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid Linked = new("10000000-0000-0000-0000-00000000000d");
    private static readonly Guid InBlock = new("10000000-0000-0000-0000-00000000000e");
    private static readonly Guid InMarkup = new("10000000-0000-0000-0000-00000000000f");
    private static readonly Guid InJson = new("10000000-0000-0000-0000-000000000010");
    private static readonly Guid Image = new("20000000-0000-0000-0000-000000000001");
    private static readonly Guid MediaLink = new("20000000-0000-0000-0000-000000000003");
    private static readonly Guid Block = new("30000000-0000-0000-0000-000000000001");

    private static readonly IApiContentRoute Route = new ApiContentRoute("/x/", new ApiContentStartItem(Guid.Empty, "site"));

    private static ApiContent Content(Guid id, Dictionary<string, object?>? properties = null)
        => new(id, "name", "page", DateTime.UnixEpoch, DateTime.UnixEpoch, Route, properties ?? []);

    [Fact]
    public void Collects_every_document_and_media_item_the_output_shows_and_the_types_it_renders()
    {
        var markup = $"<p><a href=\"/f/\" data-destination-id=\"{InMarkup}\" data-link-type=\"Content\">F</a>"
            + $"<a href=\"/media/3.png\" data-destination-id=\"{MediaLink}\" data-link-type=\"Media\">3</a></p>";
        var json = new RichTextRootElement(
            [],
            [new RichTextGenericElement("a", new Dictionary<string, object> { ["destinationId"] = InJson.ToString(), ["linkType"] = "Content" }, [])],
            []);
        // Umbraco 18 adds a constructor and marks this one obsolete; 17 has only this one, and the
        // tests build against both.
#pragma warning disable CS0618
        var block = new ApiElement(Block, "linkBlock", new Dictionary<string, object?> { ["target"] = Content(InBlock) });
#pragma warning restore CS0618
        ApiContent page = Content(Page, new Dictionary<string, object?>
        {
            ["picker"] = Content(Picked),
            ["links"] = new[] { ApiLink.Content("D", null, null, Linked, "page", Route, null), ApiLink.External("Out", "https://example.com", null, null) },
            ["image"] = new[] { new ApiMedia(Image, "Image 1", "Image", "/media/1.png", ".png", 1, 1, 1, new Dictionary<string, object?>()) },
            ["blocks"] = new ApiBlockListModel([new ApiBlockItem(block, null)]),
            ["body"] = new RichTextModel { Markup = markup, Blocks = [] },
            ["bodyJson"] = json,
        });

        var keys = new HashSet<string>();
        OutputWalker.Collect(page, keys);

        Assert.Equal(
            [
                "content-10000000-0000-0000-0000-000000000002",
                "content-10000000-0000-0000-0000-00000000000a",
                "content-10000000-0000-0000-0000-00000000000d",
                "content-10000000-0000-0000-0000-00000000000e",
                "content-10000000-0000-0000-0000-00000000000f",
                "content-10000000-0000-0000-0000-000000000010",
                "media-20000000-0000-0000-0000-000000000001",
                "media-20000000-0000-0000-0000-000000000003",
            ],
            keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Walks_the_items_of_a_paged_listing()
    {
        var listing = new PagedViewModel<ApiContent> { Total = 2, Items = [Content(Picked), Content(Linked)] };

        var keys = new HashSet<string>();
        OutputWalker.Collect(listing, keys);

        Assert.Equal(["content-10000000-0000-0000-0000-00000000000a", "content-10000000-0000-0000-0000-00000000000d"], keys.Order(StringComparer.Ordinal));
    }
}
